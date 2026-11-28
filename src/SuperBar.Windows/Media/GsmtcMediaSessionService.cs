using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;
using SuperBar.Core.Shell;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace SuperBar.Windows.Media;

/// <summary>
/// <see cref="IMediaSessionService"/> over the Global System Media Transport Controls: the same "now playing" source
/// as the Windows media flyout. Follows the current session and its media properties, playback state and timeline.
/// <para>
/// WinRT raises the session events on thread-pool threads. Each event re-reads only its part (properties, playback
/// or timeline) into the pending state under a lock, then the snapshot is published to the UI thread in order.
/// Calls into a session whose app just closed throw <c>COMException</c>s; they are caught and treated as "nothing
/// to read" until <c>CurrentSessionChanged</c> moves on. Thumbnail reads are versioned so that, when tracks are
/// skipped quickly, a slow read for an earlier track never overwrites the current one.
/// </para>
/// </summary>
internal sealed class GsmtcMediaSessionService : IMediaSessionService, IDisposable
{
    private const int MaxThumbnailBytes = 4 * 1024 * 1024;

    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GsmtcMediaSessionService> _logger;
    private readonly Lock _gate = new();
    private SessionManager? _manager;
    private Session? _session;
    private MediaSnapshot _pending = MediaSnapshot.None;
    private int _propertiesVersion;
    private bool _disposed;

    public GsmtcMediaSessionService(IUiDispatcher dispatcher, ILogger<GsmtcMediaSessionService> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _ = StartAsync();
    }

    public MediaSnapshot Current { get; private set; } = MediaSnapshot.None;

    public event EventHandler<MediaSnapshot>? Changed;

    public Task TogglePlayPauseAsync() => ControlAsync("toggle play/pause", s => s.TryTogglePlayPauseAsync().AsTask());

    public Task NextAsync() => ControlAsync("skip to the next track", s => s.TrySkipNextAsync().AsTask());

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        if (_manager is { } manager)
        {
            manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        Attach(null);
    }

    private async Task StartAsync()
    {
        try
        {
            var manager = await SessionManager.RequestAsync();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _manager = manager;
            }

            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            Attach(manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            // No media controls (for example a stripped-down Windows edition): the widget just shows nothing.
            _logger.LogWarning(ex, "Media session manager unavailable");
        }
    }

    private void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args)
    {
        try
        {
            Attach(sender.GetCurrentSession());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the current media session");
            Attach(null);
        }
    }

    private void Attach(Session? session)
    {
        Session? previous;
        lock (_gate)
        {
            if (_disposed && session is not null)
            {
                return;
            }

            previous = _session;
            if (ReferenceEquals(previous, session))
            {
                return;
            }

            _session = session;
            _propertiesVersion++; // reads still running for the previous session are dropped
            _pending = session is null ? MediaSnapshot.None : MediaSnapshot.None with { HasSession = true };
        }

        if (previous is not null)
        {
            previous.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            previous.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            previous.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            _logger.LogDebug("Following media session of {App}", SafeAppId(session));
            ReadPlayback(session);
            ReadTimeline(session);
            _ = ReadPropertiesAsync(session);
        }

        Publish();
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) => _ = ReadPropertiesAsync(sender);

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args)
    {
        ReadPlayback(sender);
        Publish();
    }

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args)
    {
        ReadTimeline(sender);
        Publish();
    }

    private void ReadPlayback(Session session)
    {
        try
        {
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Update(session, s => s with { IsPlaying = playing });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the playback state");
        }
    }

    private void ReadTimeline(Session session)
    {
        try
        {
            var t = session.GetTimelineProperties();
            if (t is null)
            {
                return;
            }

            var duration = t.EndTime - t.StartTime;
            var position = t.Position - t.StartTime;
            Update(session, s => s with
            {
                Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                Position = position > TimeSpan.Zero ? position : TimeSpan.Zero,
                PositionUpdatedAt = t.LastUpdatedTime,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the media timeline");
        }
    }

    private async Task ReadPropertiesAsync(Session session)
    {
        int version;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            version = ++_propertiesVersion;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is null)
            {
                return;
            }

            var (bytes, key) = await ReadThumbnailAsync(properties.Thumbnail);
            lock (_gate)
            {
                // A newer read started meanwhile (fast skipping) or the session changed: this result is stale.
                if (version != _propertiesVersion || !ReferenceEquals(_session, session))
                {
                    return;
                }

                _pending = _pending with
                {
                    Title = properties.Title ?? "",
                    Artist = string.IsNullOrEmpty(properties.Artist) ? properties.AlbumArtist ?? "" : properties.Artist,
                    ThumbnailBytes = bytes,
                    ThumbnailKey = key,
                };
            }

            Publish();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the media properties");
        }
    }

    private static async Task<(byte[]? Bytes, string? Key)> ReadThumbnailAsync(global::Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return (null, null);
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream is null || stream.Size == 0 || stream.Size > MaxThumbnailBytes)
            {
                return (null, null);
            }

            using var source = stream.AsStreamForRead();
            using var copy = new MemoryStream((int)stream.Size);
            await source.CopyToAsync(copy);
            var bytes = copy.ToArray();
            return (bytes, Convert.ToHexString(SHA256.HashData(bytes), 0, 8));
        }
        catch (Exception)
        {
            // No artwork is better than no title: the properties are still published.
            return (null, null);
        }
    }

    private void Update(Session session, Func<MediaSnapshot, MediaSnapshot> change)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _pending = change(_pending);
            }
        }
    }

    private void Publish()
    {
        MediaSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            snapshot = _pending;
        }

        // Queued in the order the snapshots were taken, so the UI never goes back to an older one.
        _dispatcher.TryEnqueue(() =>
        {
            if (Equals(snapshot, Current) || _disposed)
            {
                return;
            }

            Current = snapshot;
            Changed?.Invoke(this, snapshot);
        });
    }

    private async Task ControlAsync(string what, Func<Session, Task<bool>> command)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            await command(session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not {Action}", what);
        }
    }

    private static string SafeAppId(Session session)
    {
        try
        {
            return session.SourceAppUserModelId;
        }
        catch (Exception)
        {
            return "?";
        }
    }
}

using Microsoft.Extensions.Logging;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Shell;

namespace SuperBar.Widgets.BuiltIn;

/// <summary>
/// Now Playing: the system's current media (any app using the Windows media controls) with its artwork, title,
/// artist and progress, plus Play/Pause and Next. Listens only while active.
/// <para>
/// Media apps report their position only now and then, so while something plays the progress advances once a
/// second from the last reported position (<see cref="MediaSnapshot.PositionUpdatedAt"/>) and snaps to every new
/// report. Pausing freezes it where it is, and resuming continues from there.
/// </para>
/// </summary>
[Widget("superbar.media", "Now Playing",
    Description = "The song or video playing now, with play/pause and next.",
    Glyph = "\uE8D6",
    DefaultWidth = 250,
    MinWidth = 180,
    MaxWidth = 340,
    AllowMultiple = false)]
public sealed class MediaWidget : WidgetBase<MediaSettings, MediaModel>
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly IMediaSessionService _media;
    private readonly TimeProvider _time;
    private IWidgetScope? _scope;
    private MediaSnapshot _snapshot = MediaSnapshot.None;

    // The progress is anchored at (_basePosition, _baseAt) and advances from there while playing.
    private TimeSpan _basePosition;
    private DateTimeOffset _baseAt;

    // Identifies the current once-a-second tick chain; changing it orphans a pending tick (same idea as the clock).
    private int _ticks;

    public MediaWidget(WidgetContext context, IMediaSessionService media, TimeProvider? time = null)
        : base(context, new MediaModel())
    {
        _media = media;
        _time = time ?? TimeProvider.System;
        On(Model.PlayPause, () => Send(_media.TogglePlayPauseAsync(), "play/pause"));
        On(Model.Next, () => Send(_media.NextAsync(), "next"));
    }

    protected override void OnActivated(IWidgetScope scope)
    {
        _scope = scope;
        scope.Listen<MediaSnapshot>(h => _media.Changed += h, h => _media.Changed -= h, Show);
        Show(_media.Current);
    }

    protected override void OnDeactivated() => _scope = null; // the scope already stopped the ticks and the listener

    protected override void OnSettingsChanged(MediaSettings previous) => Model.SetCollapsed(Settings.HideWhenIdle && !_snapshot.HasSession);

    private void Show(MediaSnapshot snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;
        var now = _time.GetUtcNow();

        if (snapshot.PositionUpdatedAt != previous.PositionUpdatedAt || snapshot.Position != previous.Position || !snapshot.HasSession)
        {
            // A new report from the app: snap to it.
            _basePosition = snapshot.Position;
            _baseAt = snapshot.PositionUpdatedAt == default ? now : snapshot.PositionUpdatedAt;
        }
        else if (snapshot.IsPlaying != previous.IsPlaying)
        {
            // Paused or resumed without a new report: continue from where the bar shows it, not from a stale report.
            _basePosition = Elapsed(previous, now);
            _baseAt = now;
        }

        Model.Show(snapshot, Settings.HideWhenIdle);
        UpdateProgress(now);
        RestartTicks();
    }

    private void RestartTicks()
    {
        var chain = ++_ticks;
        if (_scope is { } scope && _snapshot.IsPlaying && _snapshot.Duration > TimeSpan.Zero)
        {
            Tick(scope, chain);
        }
    }

    private void Tick(IWidgetScope scope, int chain) =>
        scope.After(TickInterval, () =>
        {
            if (chain != _ticks || !ReferenceEquals(scope, _scope))
            {
                return;
            }

            UpdateProgress(_time.GetUtcNow());
            Tick(scope, chain);
        });

    private void UpdateProgress(DateTimeOffset now) =>
        Model.SetProgress(_snapshot.Duration > TimeSpan.Zero ? Elapsed(_snapshot, now) / _snapshot.Duration : 0);

    private TimeSpan Elapsed(MediaSnapshot snapshot, DateTimeOffset now)
    {
        var position = _basePosition;
        if (snapshot.IsPlaying && now > _baseAt)
        {
            position += now - _baseAt;
        }

        return position < TimeSpan.Zero ? TimeSpan.Zero : position > snapshot.Duration && snapshot.Duration > TimeSpan.Zero ? snapshot.Duration : position;
    }

    private void Send(Task command, string what) =>
        command.ContinueWith(
            t => Logger.LogDebug(t.Exception, "Media command {Command} failed", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

public sealed class MediaSettings
{
    [Setting("Hide when nothing is playing", Description = "Takes no room on the bar while no app has media open.")]
    public bool HideWhenIdle { get; set; } = true;
}

/// <summary>Presentation state of <see cref="MediaWidget"/>.</summary>
public sealed class MediaModel : ObservableObject, ICollapsibleWidgetModel
{
    private bool _hasSession;
    private bool _isPlaying;
    private bool _isCollapsed;
    private string _title = "";
    private string _artist = "";
    private string _toolTip = "";
    private double _progress;
    private bool _hasProgress;
    private byte[]? _thumbnail;
    private string? _thumbnailKey;

    public WidgetCommand PlayPause { get; } = new();

    public WidgetCommand Next { get; } = new();

    public bool HasSession { get => _hasSession; private set => SetProperty(ref _hasSession, value); }

    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    public bool IsCollapsed { get => _isCollapsed; private set => SetProperty(ref _isCollapsed, value); }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public string Artist { get => _artist; private set => SetProperty(ref _artist, value); }

    public string ToolTip { get => _toolTip; private set => SetProperty(ref _toolTip, value); }

    /// <summary>0..1.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    /// <summary>The source reports a duration; without one the progress bar is hidden.</summary>
    public bool HasProgress { get => _hasProgress; private set => SetProperty(ref _hasProgress, value); }

    /// <summary>Encoded artwork (PNG/JPEG), or null for the fallback icon.</summary>
    public byte[]? Thumbnail { get => _thumbnail; private set => SetProperty(ref _thumbnail, value); }

    /// <summary>Changes exactly when the artwork changes; views decode only then.</summary>
    public string? ThumbnailKey { get => _thumbnailKey; private set => SetProperty(ref _thumbnailKey, value); }

    internal void Show(MediaSnapshot snapshot, bool hideWhenIdle)
    {
        HasSession = snapshot.HasSession;
        IsPlaying = snapshot.IsPlaying;
        Title = snapshot.HasSession ? (snapshot.Title.Length > 0 ? snapshot.Title : "Unknown title") : "Nothing playing";
        Artist = snapshot.Artist;
        ToolTip = snapshot.HasSession ? (snapshot.Artist.Length > 0 ? $"{Title}\n{snapshot.Artist}" : Title) : "Nothing playing";
        HasProgress = snapshot.HasSession && snapshot.Duration > TimeSpan.Zero;
        if (snapshot.ThumbnailKey != ThumbnailKey)
        {
            Thumbnail = snapshot.ThumbnailBytes;
            ThumbnailKey = snapshot.ThumbnailKey;
        }

        PlayPause.IsEnabled = snapshot.HasSession;
        Next.IsEnabled = snapshot.HasSession;
        SetCollapsed(hideWhenIdle && !snapshot.HasSession);
    }

    internal void SetCollapsed(bool collapsed) => IsCollapsed = collapsed;

    internal void SetProgress(double fraction) => Progress = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
}

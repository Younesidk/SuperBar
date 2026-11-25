namespace SuperBar.Core.Shell;

/// <summary>
/// What the system's current media session is playing, as one immutable value. <see cref="Position"/> is the
/// position at <see cref="PositionUpdatedAt"/>: media apps report it only now and then, so a display interpolates.
/// </summary>
public sealed record MediaSnapshot(
    bool HasSession,
    bool IsPlaying,
    string Title,
    string Artist,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt,
    byte[]? ThumbnailBytes,
    string? ThumbnailKey)
{
    /// <summary>No media session.</summary>
    public static readonly MediaSnapshot None = new(false, false, "", "", TimeSpan.Zero, TimeSpan.Zero, default, null, null);
}

/// <summary>
/// The system's current media session (Windows: Global System Media Transport Controls). <see cref="Changed"/> is
/// raised on the UI thread, and <see cref="Current"/> is only updated there. Commands never throw: a session that
/// vanished (its app closed) is simply not controlled.
/// </summary>
public interface IMediaSessionService
{
    MediaSnapshot Current { get; }

    event EventHandler<MediaSnapshot>? Changed;

    Task TogglePlayPauseAsync();

    Task NextAsync();
}

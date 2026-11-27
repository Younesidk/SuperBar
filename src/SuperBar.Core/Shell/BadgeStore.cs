namespace SuperBar.Core.Shell;

/// <summary>An app's overlay icon (taskbar badge) on one of its windows, as saved across SuperBar restarts.</summary>
/// <param name="Window">The window handle (window handles are system-wide and outlive SuperBar).</param>
/// <param name="ProcessId">The window's process when the badge was set, so a reused handle never gets a stale badge.</param>
public sealed record SavedBadge(long Window, int ProcessId, IconPixels Icon);

/// <summary>
/// Keeps app badges across a SuperBar restart. Apps send their overlay icon only when it changes (Discord's unread
/// count, for example), so a restarted bar would otherwise show none until the next change.
/// </summary>
public interface IBadgeStore
{
    IReadOnlyList<SavedBadge> Load();

    void Save(IReadOnlyList<SavedBadge> badges);
}

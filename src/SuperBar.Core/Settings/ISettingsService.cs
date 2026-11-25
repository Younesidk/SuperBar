namespace SuperBar.Core.Settings;

/// <summary>Low-level persistent key/value storage of JSON documents.</summary>
public interface ISettingsStore
{
    string? Read(string key);
    void Write(string key, string json);
    void Delete(string key);
}

public sealed class SettingsChangedEventArgs(SuperBarSettings previous, SuperBarSettings current) : EventArgs
{
    public SuperBarSettings Previous { get; } = previous;
    public SuperBarSettings Current { get; } = current;
}

/// <summary>
/// Owns the current settings snapshot. Snapshots are never mutated after publication:
/// <see cref="Update"/> clones, applies the change, normalizes, persists and then swaps.
/// </summary>
public interface ISettingsService
{
    SuperBarSettings Current { get; }

    event EventHandler<SettingsChangedEventArgs>? Changed;

    void Update(Action<SuperBarSettings> change);

    void ResetToDefaults();
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Settings;

namespace SuperBar.Widgets;

/// <summary>
/// A widget instance's runtime state: things the widget remembers that are not user settings (a running
/// countdown, the last value fetched, a dismissed hint). Stored in its own row, so saving it never publishes a
/// settings change and never re-lays out the bar. Safe to call from any thread.
/// </summary>
public interface IWidgetStateStore
{
    /// <summary>The saved state, or a new <typeparamref name="TState"/> when nothing (valid) was saved. Read leniently, like settings.</summary>
    TState Load<TState>() where TState : class, new();

    void Save<TState>(TState state) where TState : class;

    void Clear();
}

/// <summary>Where widget state lives in the settings store.</summary>
public static class WidgetStateKeys
{
    public const string Prefix = "widget-state:";

    public static string For(string instanceId) => Prefix + instanceId;
}

/// <summary><see cref="IWidgetStateStore"/> over one row of the settings store (<c>widget-state:{instanceId}</c>).</summary>
public sealed class SettingsStoreWidgetState(ISettingsStore store, string instanceId, ILogger logger) : IWidgetStateStore
{
    private readonly string _key = WidgetStateKeys.For(instanceId);

    public TState Load<TState>() where TState : class, new()
    {
        string? json;
        try
        {
            json = store.Read(_key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the state of widget {InstanceId}", instanceId);
            return new TState();
        }

        return WidgetStateSerializer.Parse<TState>(json, logger);
    }

    public void Save<TState>(TState state) where TState : class =>
        store.Write(_key, WidgetSettingsBinder.ToJson(state).ToJsonString());

    public void Clear() => store.Delete(_key);
}

/// <summary>State kept in memory only: for tests, and for hosts without a settings store.</summary>
public sealed class InMemoryWidgetStateStore : IWidgetStateStore
{
    private readonly ConcurrentDictionary<int, string> _json = new();

    public TState Load<TState>() where TState : class, new() =>
        WidgetStateSerializer.Parse<TState>(_json.TryGetValue(0, out var json) ? json : null, null);

    public void Save<TState>(TState state) where TState : class =>
        _json[0] = WidgetSettingsBinder.ToJson(state).ToJsonString();

    public void Clear() => _json.Clear();
}

internal static class WidgetStateSerializer
{
    public static TState Parse<TState>(string? json, ILogger? logger) where TState : class, new()
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TState();
        }

        try
        {
            return WidgetSettingsBinder.Bind<TState>(JsonNode.Parse(json) as JsonObject, logger);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Widget state is corrupt; starting from a new {StateType}", typeof(TState).Name);
            return new TState();
        }
    }
}

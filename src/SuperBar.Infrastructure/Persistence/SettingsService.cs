using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Settings;

namespace SuperBar.Infrastructure.Persistence;

public sealed class SettingsService : ISettingsService
{
    internal const string MetaKey = "meta";
    internal const string AppearanceKey = "appearance";
    internal const string BehaviorKey = "behavior";
    internal const string LayoutKey = "layout";
    internal const string WidgetsKey = "widgets";
    internal const string PinnedAppsKey = "pinnedApps";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _gate = new();
    private SuperBarSettings _current;

    public SettingsService(ISettingsStore store, ILogger<SettingsService> logger)
    {
        _store = store;
        _logger = logger;
        _current = Load();
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public SuperBarSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(Action<SuperBarSettings> change)
    {
        SuperBarSettings previous, next;
        lock (_gate)
        {
            previous = _current;
            next = Clone(previous);
            change(next);
            SettingsNormalizer.Normalize(next);
            Save(next);
            _current = next;
        }

        _logger.LogDebug("Settings updated");
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
    }

    public void ResetToDefaults()
    {
        Update(s =>
        {
            var d = DefaultSettings.Create();
            s.Appearance = d.Appearance;
            s.Behavior = d.Behavior;
            s.Layout = d.Layout;
            s.Widgets = d.Widgets;
            s.PinnedApps = d.PinnedApps;
        });
    }

    public static SuperBarSettings Clone(SuperBarSettings settings) =>
        JsonSerializer.Deserialize<SuperBarSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    private SuperBarSettings Load()
    {
        if (_store.Read(MetaKey) is null)
        {
            _logger.LogInformation("No settings found; writing defaults");
            var defaults = SettingsNormalizer.Normalize(DefaultSettings.Create());
            Save(defaults);
            return defaults;
        }

        var fallback = DefaultSettings.Create();
        var settings = new SuperBarSettings
        {
            Appearance = ReadPart(AppearanceKey, fallback.Appearance),
            Behavior = ReadPart(BehaviorKey, fallback.Behavior),
            Layout = ReadPart(LayoutKey, fallback.Layout),
            Widgets = ReadPart(WidgetsKey, fallback.Widgets),
            PinnedApps = ReadPart(PinnedAppsKey, fallback.PinnedApps),
        };

        return SettingsNormalizer.Normalize(settings);
    }

    private T ReadPart<T>(string key, T fallback)
    {
        var json = _store.Read(key);
        if (json is null)
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Settings area {Key} is corrupt; using defaults for that area", key);
            return fallback;
        }
    }

    private void Save(SuperBarSettings s)
    {
        _store.Write(AppearanceKey, JsonSerializer.Serialize(s.Appearance, JsonOptions));
        _store.Write(BehaviorKey, JsonSerializer.Serialize(s.Behavior, JsonOptions));
        _store.Write(LayoutKey, JsonSerializer.Serialize(s.Layout, JsonOptions));
        _store.Write(WidgetsKey, JsonSerializer.Serialize(s.Widgets, JsonOptions));
        _store.Write(PinnedAppsKey, JsonSerializer.Serialize(s.PinnedApps, JsonOptions));
        _store.Write(MetaKey, JsonSerializer.Serialize(new { schemaVersion = s.SchemaVersion }, JsonOptions));
    }
}

namespace SuperBar.Core.Settings;

public enum ThemeMode
{
    Dark,
    Light,
}

public enum TaskbarZone
{
    Left,
    Center,
    Right,
}

public enum SectionKind
{
    Start,
    Applications,
    Widget,
    SystemTray,

    /// <summary>
    /// Legacy (schema 1): the clock was a built-in section. It is a widget now; <see cref="SettingsNormalizer"/>
    /// rewrites these entries. The member stays so that settings saved by older versions still load.
    /// </summary>
    Clock,
}

public enum MonitorMode
{
    /// <summary>Milestone 1: SuperBar replaces the taskbar on the primary monitor only.</summary>
    PrimaryOnly,

    /// <summary>Reserved for a future milestone (one bar per monitor).</summary>
    AllMonitors,
}

public sealed class AppearanceSettings
{
    public const double MinHeight = 32;
    public const double MaxHeight = 96;

    public double Height { get; set; } = 52;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public double BackgroundOpacity { get; set; } = 0.82;
    public bool UseAcrylic { get; set; } = true;
    public string AccentColor { get; set; } = "#5B8CFF";
    public double ItemCornerRadius { get; set; } = 10;
    public double ItemSpacing { get; set; } = 6;
    public double EdgePadding { get; set; } = 8;
}

public sealed class BehaviorSettings
{
    /// <summary>Hide the native Windows taskbar while SuperBar runs (restored on exit).</summary>
    public bool HideNativeTaskbar { get; set; } = true;

    /// <summary>Not implemented yet; persisted so the Control Center can show it.</summary>
    public bool StartWithWindows { get; set; }

    public MonitorMode MonitorMode { get; set; } = MonitorMode.PrimaryOnly;
}

public sealed class SectionPlacement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public SectionKind Kind { get; set; }
    public TaskbarZone Zone { get; set; }

    /// <summary>Only used when <see cref="Kind"/> is <see cref="SectionKind.Widget"/>.</summary>
    public string? WidgetInstanceId { get; set; }
}

public sealed class WidgetInstanceSettings
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public string TypeId { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Requested width in DIPs; clamped to the widget type's min/max at runtime. Null = type default.</summary>
    public double? Width { get; set; }

    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class PinnedApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";

    /// <summary>Full path to an executable (or shortcut / shell target).</summary>
    public string Path { get; set; } = "";

    public string? Arguments { get; set; }
}

/// <summary>Root settings aggregate. Treated as an immutable snapshot once published by the settings service.</summary>
public sealed class SuperBarSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public AppearanceSettings Appearance { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public List<SectionPlacement> Layout { get; set; } = new();
    public List<WidgetInstanceSettings> Widgets { get; set; } = new();
    public List<PinnedApp> PinnedApps { get; set; } = new();
}

using System.Text.Json.Nodes;

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

    /// <summary>Start SuperBar at sign-in. The app applies it to Windows' per-user Run key (installed builds only).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Show the apps pinned to the Windows taskbar, in the same order, and follow live as they are pinned, unpinned or
    /// reordered in Windows. Pins made in SuperBar stay either way.
    /// </summary>
    public bool SyncWindowsPins { get; set; } = true;

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

    /// <summary>
    /// The widget type's own settings, as JSON. Values are typed (numbers, booleans) since schema 2; rows saved by
    /// schema 1 hold strings, which the widget settings binder converts. Owned by the widget type: the rest of
    /// SuperBar never interprets it.
    /// </summary>
    public JsonObject Options { get; set; } = new();
}

/// <summary>
/// An application pinned to the bar. A pin comes from SuperBar itself, from the Windows taskbar (kept in sync), or
/// both; it stays on the bar while either still pins it. Pins are matched to running windows by
/// <c>PinMatching</c> and keyed by <c>AppOrdering.PinKey</c>.
/// </summary>
public sealed class PinnedApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Full path to an executable (or shortcut / shell target). A packaged (Store / MSIX) app is
    /// <c>shell:AppsFolder\&lt;AppUserModelID&gt;</c>.
    /// </summary>
    public string Path { get; set; } = "";

    public string? Arguments { get; set; }

    /// <summary>The working directory the pin's shortcut starts the app in, if it names one.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// The app's explicit AppUserModelID: every packaged app has one, and so do desktop apps whose shortcut sets one
    /// (browser profiles, web apps, Electron apps). Windows groups windows by it, and so does SuperBar.
    /// </summary>
    public string? AppUserModelId { get; set; }

    /// <summary>
    /// The shortcut Windows keeps for this pin (<c>User Pinned\TaskBar\*.lnk</c>). Launched as it is, so its arguments,
    /// working directory and "Run as administrator" flag apply exactly as on the Windows taskbar.
    /// </summary>
    public string? ShortcutPath { get; set; }

    /// <summary>Pinned on the Windows taskbar, as of the last sync.</summary>
    public bool PinnedInWindows { get; set; }

    /// <summary>Pinned in SuperBar itself. True for every pin saved before Windows pins were synced.</summary>
    public bool PinnedInSuperBar { get; set; } = true;

    /// <summary>
    /// Unpinned in SuperBar while still pinned in Windows. The entry is kept, hidden, so the next sync does not bring it
    /// back; it goes away when the app is unpinned in Windows too (pinning it there again shows it again).
    /// </summary>
    public bool HiddenFromBar { get; set; }

    /// <summary>
    /// The pin's position on the Windows taskbar at the last sync. Comparing it with the next sync tells whether the
    /// user reordered pins in Windows (then SuperBar follows) or only in SuperBar (then SuperBar keeps its own order).
    /// </summary>
    public int? WindowsOrder { get; set; }

    /// <summary>A copy (every member is a value or a string).</summary>
    public PinnedApp Clone() => (PinnedApp)MemberwiseClone();
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

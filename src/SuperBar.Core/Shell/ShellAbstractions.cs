using SuperBar.Core.Geometry;

namespace SuperBar.Core.Shell;

// ---------------------------------------------------------------------------------------------
// Windows shell integration contracts. Implemented by SuperBar.Windows.
// All members are expected to be called on the UI thread unless stated otherwise; events are
// raised on the UI thread because the native message pumps live there.
// ---------------------------------------------------------------------------------------------

public enum ScreenEdge
{
    Bottom,
    Top,
}

/// <summary>A physical display.</summary>
public sealed record MonitorInfo(
    nint Handle,
    string DeviceName,
    ScreenRect Bounds,
    ScreenRect WorkArea,
    double Scale,
    bool IsPrimary);

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    MonitorInfo GetPrimaryMonitor();
}

/// <summary>Options used to reserve a strip of a monitor for the SuperBar window.</summary>
public sealed record AppBarOptions(ScreenEdge Edge, double ThicknessDip, nint MonitorHandle);

/// <summary>
/// Registers windows as shell AppBars (SHAppBarMessage ABM_NEW / ABM_QUERYPOS / ABM_SETPOS / ABM_REMOVE),
/// so the desktop work area excludes the bar and maximized windows do not cover it.
/// </summary>
public interface IAppBarService
{
    IAppBarRegistration Register(nint windowHandle, AppBarOptions options);
}

public interface IAppBarRegistration : IDisposable
{
    /// <summary>The rectangle (physical pixels) currently reserved and occupied by the bar.</summary>
    ScreenRect Bounds { get; }

    /// <summary>True while a full-screen application is active on the bar's monitor.</summary>
    bool IsFullscreenAppActive { get; }

    event EventHandler<ScreenRect>? BoundsChanged;
    event EventHandler<bool>? FullscreenAppChanged;

    void Update(AppBarOptions options);

    /// <summary>Re-negotiates the position with the shell (e.g. after a display change).</summary>
    void Reposition();

    /// <summary>Re-sends ABM_NEW; required after Explorer restarts because the shell forgets AppBars.</summary>
    void Reregister();
}

public enum NativeTaskbarState
{
    Visible,
    Hidden,
}

/// <summary>
/// Hides and restores the native Windows taskbar without killing Explorer.
/// Every change is recorded so it can be reverted on exit or on the next launch after a crash.
/// </summary>
public interface INativeTaskbarController
{
    NativeTaskbarState State { get; }

    void Hide();

    /// <summary>Restores the native taskbar to the state recorded before <see cref="Hide"/>. Safe to call from any thread and more than once.</summary>
    void Restore();

    /// <summary>Restores a taskbar left hidden by a previous SuperBar session that did not exit cleanly.</summary>
    bool RecoverFromPreviousSession();
}

/// <summary>Persisted record of changes SuperBar made to the native taskbar.</summary>
public sealed record NativeTaskbarRecoveryRecord(
    bool TaskbarHidden,
    bool AutoHideChangedBySuperBar,
    int OriginalAppBarState,
    int ProcessId,
    DateTimeOffset ChangedAtUtc);

public interface ITaskbarRecoveryStore
{
    NativeTaskbarRecoveryRecord? Load();
    void Save(NativeTaskbarRecoveryRecord record);
    void Clear();
}

/// <summary>Global shell notifications that are not tied to a specific SuperBar window.</summary>
public interface IShellEvents
{
    /// <summary>Raised when Explorer (re)creates its taskbar, i.e. after an Explorer restart.</summary>
    event EventHandler? ExplorerRestarted;

    /// <summary>Raised on resolution, monitor layout or DPI changes.</summary>
    event EventHandler? DisplaySettingsChanged;
}

/// <summary>A top-level window that would appear on a taskbar.</summary>
/// <param name="IsFlashing">The window asked for attention (FlashWindowEx) and has not been activated since.</param>
/// <param name="OverlayIcon">The badge the app put on its taskbar button (ITaskbarList3::SetOverlayIcon), if any.</param>
public sealed record TrackedWindow(
    nint Handle,
    string Title,
    int ProcessId,
    string? ProcessPath,
    bool IsFlashing = false,
    IconPixels? OverlayIcon = null);

public interface IWindowTracker
{
    IReadOnlyList<TrackedWindow> Windows { get; }

    /// <summary>The most recently activated tracked window (SuperBar's own windows are ignored).</summary>
    nint ActiveWindow { get; }

    /// <summary>Raised when windows are added, removed, retitled, activated, start flashing or change their badge.</summary>
    event EventHandler? WindowsChanged;

    void Start();

    /// <summary>Excludes one of SuperBar's own surfaces (e.g. the bar itself) from tracking.</summary>
    void Ignore(nint windowHandle);
}

public interface IWindowSwitcher
{
    /// <summary>Activates the window, or minimizes it if it is already the active window.</summary>
    void ActivateOrMinimize(nint windowHandle, bool isActive);

    void Activate(nint windowHandle);

    /// <summary>Asks the window to close, as the taskbar's "Close window" does (the app may prompt or refuse).</summary>
    void Close(nint windowHandle);
}

/// <summary>The mouse pointer's position on screen, in physical pixels.</summary>
public interface ICursorService
{
    bool TryGetPosition(out int x, out int y);
}

/// <summary>Live DWM thumbnails of other windows, drawn into one of SuperBar's own windows.</summary>
public interface IWindowThumbnailService
{
    /// <summary>Registers a thumbnail of <paramref name="sourceWindow"/> inside <paramref name="destinationWindow"/>; null when DWM refuses.</summary>
    IWindowThumbnail? Create(nint destinationWindow, nint sourceWindow);
}

/// <summary>One registered thumbnail. Dispose to unregister it.</summary>
public interface IWindowThumbnail : IDisposable
{
    /// <summary>The source window's size in physical pixels; empty when unknown.</summary>
    PixelSize SourceSize { get; }

    /// <summary>Draws the thumbnail at <paramref name="destination"/>: physical pixels relative to the destination window's client area.</summary>
    void Show(ScreenRect destination);
}

public interface IAppLauncher
{
    bool Launch(string path, string? arguments = null);
}

/// <summary>Straight (non-premultiplied) BGRA32 pixels, top-down.</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

public interface IIconProvider
{
    IconPixels? GetWindowIcon(nint windowHandle);
    IconPixels? GetFileIcon(string path, int sizePx);
}

/// <summary>Shell surfaces that SuperBar opens rather than re-implements.</summary>
public interface IShellCommands
{
    /// <summary>Opens Start, or closes it when it is already open (including when the click on the bar is what dismissed it).</summary>
    void ToggleStartMenu();

    /// <summary>Opens or closes Explorer's "hidden icons" notification-area flyout next to the bar.</summary>
    void ToggleTrayOverflow();
    /// <summary>Opens or closes the Windows notification center / calendar flyout.</summary>
    void ToggleNotificationCenter();

    /// <summary>Opens or closes the Windows Quick Settings flyout.</summary>
    void ToggleQuickSettings();
    void OpenTaskManager();
}

public readonly record struct SystemResourceSnapshot(double CpuPercent, double MemoryPercent, ulong MemoryUsedBytes, ulong MemoryTotalBytes);

public interface ISystemMetricsProvider
{
    /// <summary>Samples CPU (delta since the previous call) and memory usage. Cheap; no background threads.</summary>
    SystemResourceSnapshot Sample();
}

/// <summary>
/// Win32 window-chrome tweaks the UI framework cannot express. Keeps P/Invoke out of the UI projects:
/// they pass the native handle of their window and never touch Win32 themselves.
/// </summary>
public interface IWindowChrome
{
    /// <summary>
    /// Turns a top-level window into a shell surface such as the taskbar: a tool window (never in Alt+Tab, Win+Tab or
    /// the taskbar), no caption or sizing frame, no Windows 11 rounded corners or DWM border, and no minimum-size clamp
    /// (so the AppBar can size it down to <c>AppearanceSettings.MinHeight</c> at any DPI).
    /// The window stays activatable, so clicks, flyouts and <c>SetForegroundWindow</c> keep working.
    /// </summary>
    /// <param name="roundedCorners">Keep the Windows 11 rounded corners and outline, for popups such as the window preview.</param>
    void ApplyShellSurfaceStyle(nint windowHandle, bool roundedCorners = false);

    /// <summary>Shows the window without activating it or stealing focus (SW_SHOWNOACTIVATE).</summary>
    void ShowWithoutActivation(nint windowHandle);

    /// <summary>
    /// Sets the whole window's opacity (0..1), content, backdrop, DWM thumbnails and outline included. Used to animate
    /// popups. The window becomes a layered window on first use.
    /// </summary>
    void SetOpacity(nint windowHandle, double opacity);

    /// <summary>Moves the window without resizing, activating or reordering it (physical pixels).</summary>
    void MoveTo(nint windowHandle, int x, int y);

    /// <summary>The window's DPI scale (1.0 = 96 DPI).</summary>
    double GetScale(nint windowHandle);

    /// <summary>
    /// Gives the window its large and small icon (<c>WM_SETICON</c>, <c>ICON_BIG</c> and <c>ICON_SMALL</c>) from an
    /// .ico file, each loaded at the exact size the window's DPI calls for. The title bar and Alt+Tab read these.
    /// </summary>
    /// <param name="iconPath">Absolute path of the .ico file.</param>
    /// <returns><see langword="false"/> when the file could not be loaded at either size.</returns>
    bool SetWindowIcon(nint windowHandle, string iconPath);
}

/// <summary>What a Bluetooth device is, as far as its icon is concerned.</summary>
public enum BluetoothDeviceKind
{
    Unknown,
    Headphones,
    Earbuds,
    Mouse,
    Keyboard,
    Gamepad,
    Speaker,
}

/// <summary>A connected Bluetooth device that reports its battery level. <paramref name="Id"/> is its PnP container id.</summary>
public sealed record BluetoothBatteryDevice(string Id, string Name, int BatteryPercent, BluetoothDeviceKind Kind);

/// <summary>
/// Battery levels of the connected Bluetooth devices (Classic and LE) that report one, as Windows itself knows them
/// (the same values as Settings → Bluetooth &amp; devices). Never connects to a device. Safe to call from any thread.
/// </summary>
public interface IBluetoothBatteryService
{
    /// <summary>
    /// One entry per connected device that reports a 0..100 battery level, lowest level first, then by name.
    /// Empty when there is no radio, Bluetooth is off or nothing is connected; never throws for those.
    /// </summary>
    Task<IReadOnlyList<BluetoothBatteryDevice>> GetConnectedDevicesAsync(CancellationToken cancellationToken);
}

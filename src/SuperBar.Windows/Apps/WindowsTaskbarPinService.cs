using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;

namespace SuperBar.Windows.Apps;

/// <summary>
/// Reads the Windows taskbar's pins. The <c>Taskband\Favorites</c> registry value is the authoritative list and order,
/// and the only place packaged (Store / MSIX) pins appear. Desktop pins point at the .lnk files in
/// <c>User Pinned\TaskBar</c>, which carry the target, arguments, working directory and AppUserModelID; that folder is
/// also the fallback when the registry value is missing or damaged. All shell work runs on a short-lived STA thread.
/// </summary>
internal sealed partial class WindowsTaskbarPinService : IWindowsTaskbarPinService, IDisposable
{
    internal const string TaskbandKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
    private const string FavoritesValue = "Favorites";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RegistryRetry = TimeSpan.FromSeconds(5);

    /// <summary>System.Link.TargetParsingPath: what an AppsFolder entry for a desktop app starts.</summary>
    private static PROPERTYKEY PKEY_Link_TargetParsingPath => new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    private readonly ILogger<WindowsTaskbarPinService> _logger;
    private readonly Lock _gate = new();
    private Timer? _debounce;
    private FileSystemWatcher? _folderWatcher;
    private Thread? _registryThread;
    private ManualResetEvent? _stop;

    public WindowsTaskbarPinService(ILogger<WindowsTaskbarPinService> logger) => _logger = logger;

    public event EventHandler? PinsChanged;

    internal static string PinnedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    // --- Reading ---------------------------------------------------------------------------------------------------

    public Task<WindowsPinReadResult?> ReadAsync(int iconSizePx, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<WindowsPinReadResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(cancellationToken.IsCancellationRequested ? null : Read(iconSizePx));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading the Windows taskbar pins failed");
                completion.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "SuperBar Windows pins",
        };

        // Shell items, shortcuts and image factories are apartment-threaded objects.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    private WindowsPinReadResult? Read(int iconSizePx)
    {
        var folder = PinnedFolder;
        var shortcuts = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.lnk") : [];
        var favorites = ReadFavorites();
        if (favorites is null && shortcuts.Length == 0)
        {
            _logger.LogInformation("Windows taskbar pins not readable yet: no Taskband value and no pinned shortcuts");
            return null;
        }

        var skipped = new List<string>();
        var pins = new List<WindowsTaskbarPin>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;

        var wellFormed = false;
        var entries = favorites is null ? [] : TaskbandFavorites.Split(favorites, out wellFormed);
        if (favorites is null)
        {
            complete = false;
            _logger.LogInformation("The Taskband registry value is missing; reading the pinned shortcuts folder instead");
        }
        else if (!wellFormed)
        {
            complete = false;
            _logger.LogWarning("The Taskband registry value is damaged after {Count} entries; the pinned shortcuts folder fills in", entries.Count);
        }

        foreach (var entry in entries)
        {
            var pin = ReadTaskbandEntry(entry, iconSizePx, skipped, ref complete);
            if (pin is null)
            {
                continue;
            }

            if (pin.ShortcutPath is { } lnk && !used.Add(lnk))
            {
                continue; // the same shortcut twice
            }

            pins.Add(pin);
        }

        // The folder fills the gaps when the registry could not account for every pin; otherwise it only cross-checks.
        var fromFolder = 0;
        var unlisted = shortcuts.Where(s => !used.Contains(s)).OrderBy(File.GetCreationTimeUtc).ToList();
        if (!complete)
        {
            foreach (var lnk in unlisted)
            {
                if (ReadShortcut(lnk, null, iconSizePx, skipped) is { } pin)
                {
                    pins.Add(pin);
                    fromFolder++;
                }
            }
        }
        else if (unlisted.Count > 0)
        {
            _logger.LogDebug("Ignored {Count} shortcut(s) in the pinned folder that Windows does not list as pinned: {Names}",
                unlisted.Count, string.Join(", ", unlisted.Select(Path.GetFileName)));
        }

        _logger.LogInformation(
            "Windows taskbar pins: {Count} read ({Taskband} Taskband entries, {Shortcuts} shortcuts in the pinned folder, {FromFolder} taken from the folder), {Skipped} skipped{Partial}",
            pins.Count, entries.Count, shortcuts.Length, fromFolder, skipped.Count, complete ? "" : ", partial read");
        foreach (var reason in skipped)
        {
            _logger.LogInformation("Skipped Windows pin: {Reason}", reason);
        }

        return new WindowsPinReadResult(pins, complete, entries.Count, shortcuts.Length, skipped);
    }

    private static byte[]? ReadFavorites()
    {
        using var key = Registry.CurrentUser.OpenSubKey(TaskbandKeyPath);
        return key?.GetValue(FavoritesValue) as byte[];
    }

    private WindowsTaskbarPin? ReadTaskbandEntry(byte[] idList, int iconSizePx, List<string> skipped, ref bool complete)
    {
        var pidl = Marshal.AllocCoTaskMem(idList.Length);
        try
        {
            Marshal.Copy(idList, 0, pidl, idList.Length);
            var display = ShellInterop.GetName(pidl, ShellInterop.SIGDN_NORMALDISPLAY);
            if (display is null)
            {
                // The shell cannot resolve it (yet): an app still registering at boot, or one being uninstalled.
                skipped.Add("an entry the shell cannot resolve (app not registered yet or uninstalled)");
                complete = false;
                return null;
            }

            var fileSystemPath = ShellInterop.GetName(pidl, ShellInterop.SIGDN_FILESYSPATH);
            if (fileSystemPath is not null && fileSystemPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(fileSystemPath))
                {
                    skipped.Add($"{display}: its shortcut {fileSystemPath} is gone");
                    return null;
                }

                return ReadShortcut(fileSystemPath, display, iconSizePx, skipped);
            }

            return ReadAppsFolderItem(pidl, display, iconSizePx, skipped);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            skipped.Add($"an entry that failed to read ({ex.GetType().Name}: {ex.Message})");
            complete = false;
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>A pin of the Start menu's app list: a packaged app, or a desktop app pinned by its id.</summary>
    private static WindowsTaskbarPin? ReadAppsFolderItem(nint pidl, string display, int iconSizePx, List<string> skipped)
    {
        string? appId = null;
        string? target = null;
        if (ShellInterop.SHGetPropertyStoreFromIDList(pidl, ShellInterop.GPS_DEFAULT, ShellInterop.IID_IPropertyStore, out var store) == 0)
        {
            try
            {
                appId = ShellInterop.GetString(store, ShellInterop.PKEY_AppUserModel_ID);
                target = ShellInterop.GetString(store, PKEY_Link_TargetParsingPath);
            }
            finally
            {
                ShellInterop.Release(store);
            }
        }

        appId ??= ShellInterop.GetName(pidl, ShellInterop.SIGDN_PARENTRELATIVEPARSING);
        if (string.IsNullOrWhiteSpace(appId))
        {
            skipped.Add($"{display}: neither a shortcut nor an app id");
            return null;
        }

        var icon = ShellImages.FromIdList(pidl, iconSizePx);
        var appsFolderPath = PinMatching.AppsFolderPath(appId);

        // A desktop app listed by its path ("{known folder}\vendor\app.exe") has no explicit id: Windows groups its
        // windows by executable, and so must SuperBar.
        var exe = ResolveDesktopAppPath(appId) ?? (IsExistingExecutable(target) ? target : null);
        if (exe is not null && !PinMatching.IsPackagedAppId(appId) && ImplicitIdRegex().IsMatch(appId))
        {
            return new WindowsTaskbarPin(display, exe, null, null, null, null, appsFolderPath, icon);
        }

        return new WindowsTaskbarPin(display, exe ?? appsFolderPath, null, null, null, appId, appsFolderPath, icon);
    }

    private WindowsTaskbarPin? ReadShortcut(string lnkPath, string? display, int iconSizePx, List<string> skipped)
    {
        display ??= Path.GetFileNameWithoutExtension(lnkPath);
        try
        {
            var link = ReadLink(lnkPath);

            // Pinned from the Start menu, a pin can be a shortcut to the Start menu's shortcut (JetBrains Toolbox does
            // this): follow it once, keeping whatever the outer shortcut sets itself.
            if (link.Target is null && link.TargetItemPath is { } inner && inner.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(inner))
            {
                var resolved = ReadLink(inner);
                link = new LinkInfo(
                    resolved.Target,
                    link.Arguments ?? resolved.Arguments,
                    link.WorkingDirectory ?? resolved.WorkingDirectory,
                    link.AppId ?? resolved.AppId,
                    resolved.TargetItemPath,
                    resolved.TargetIsShellLocation);
            }

            var target = link.Target;
            if (string.IsNullOrEmpty(target))
            {
                // A shortcut to a shell location rather than a file (File Explorer's points at Home or "This PC"):
                // it opens in Explorer, and its windows are Explorer's.
                target = link.TargetIsShellLocation
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                    : link.TargetItemPath ?? lnkPath;
            }
            else if (IsMissingLocalFile(target))
            {
                skipped.Add($"{display}: its target {target} no longer exists");
                return null;
            }

            var icon = ShellImages.FromParsingName(lnkPath, iconSizePx);
            return new WindowsTaskbarPin(display, target, link.Arguments, link.WorkingDirectory, lnkPath, link.AppId, lnkPath, icon);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            skipped.Add($"{display}: its shortcut could not be read ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <param name="Target">The target file, when the shortcut points at one.</param>
    /// <param name="TargetItemPath">The file system path of the shortcut's target item (set for a link to a link).</param>
    /// <param name="TargetIsShellLocation">The target is a shell location or folder, which opens in Explorer.</param>
    private sealed record LinkInfo(
        string? Target,
        string? Arguments,
        string? WorkingDirectory,
        string? AppId,
        string? TargetItemPath,
        bool TargetIsShellLocation);

    private static LinkInfo ReadLink(string lnkPath)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellInterop.CLSID_ShellLink, throwOnError: true)!)!;
            ((IPersistFile)link).Load(lnkPath, ShellInterop.STGM_READ);
            var shell = (IShellLinkW)link;

            var buffer = new StringBuilder(2048);
            var target = shell.GetPath(buffer, buffer.Capacity, 0, 0) == 0 ? Expand(buffer) : null;
            buffer.Clear();
            var arguments = shell.GetArguments(buffer, buffer.Capacity) == 0 ? Expand(buffer) : null;
            buffer.Clear();
            var workingDirectory = shell.GetWorkingDirectory(buffer, buffer.Capacity) == 0 ? Expand(buffer) : null;
            var appId = link is IPropertyStore store ? ShellInterop.GetString(store, ShellInterop.PKEY_AppUserModel_ID) : null;

            string? itemPath = null;
            var shellLocation = false;
            if (target is null && shell.GetIDList(out var idList) == 0 && idList != 0)
            {
                try
                {
                    // For a link to a link the shell resolves the inner one: its parsing name is then the final target
                    // (JetBrains Toolbox pins report datagrip64.exe there while their file system name fails).
                    var parsing = ShellInterop.GetName(idList, ShellInterop.SIGDN_DESKTOPABSOLUTEPARSING);
                    itemPath = ShellInterop.GetName(idList, ShellInterop.SIGDN_FILESYSPATH);
                    if (parsing is not null && !parsing.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(parsing))
                    {
                        target = parsing;
                    }
                    else
                    {
                        itemPath ??= parsing is not null && File.Exists(parsing) ? parsing : null;
                        shellLocation = parsing is not null && (parsing.StartsWith("::{", StringComparison.Ordinal) || Directory.Exists(parsing));
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(idList);
                }
            }

            return new LinkInfo(target, arguments, workingDirectory, appId, itemPath, shellLocation);
        }
        finally
        {
            ShellInterop.Release(link);
        }
    }

    private static string? Expand(StringBuilder buffer)
    {
        var text = buffer.ToString().Trim();
        return text.Length == 0 ? null : Environment.ExpandEnvironmentVariables(text);
    }

    /// <summary>
    /// The target is a local path whose drive exists but whose file does not: the app was uninstalled. A path on an
    /// unplugged drive or a network share that is offline is kept: it may come back.
    /// </summary>
    private static bool IsMissingLocalFile(string target)
    {
        try
        {
            if (!Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            var root = Path.GetPathRoot(target);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root) && !File.Exists(target) && !Directory.Exists(target);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsExistingExecutable(string? path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>"{1AC14E77-…}\notepad.exe" (a known folder, then a relative path) or a full path to an executable.</summary>
    private static string? ResolveDesktopAppPath(string appId)
    {
        var match = ImplicitIdRegex().Match(appId);
        if (!match.Success)
        {
            return null;
        }

        string? path;
        if (match.Groups["folder"].Success)
        {
            if (SHGetKnownFolderPath(new Guid(match.Groups["folder"].Value), 0, 0, out var folder) != 0 || folder == 0)
            {
                return null;
            }

            try
            {
                path = Path.Combine(Marshal.PtrToStringUni(folder)!, match.Groups["rest"].Value);
            }
            finally
            {
                Marshal.FreeCoTaskMem(folder);
            }
        }
        else
        {
            path = appId;
        }

        return IsExistingExecutable(path) ? path : null;
    }

    [GeneratedRegex(@"^(?:\{(?<folder>[0-9A-Fa-f\-]{36})\}\\(?<rest>.+\.exe)|[A-Za-z]:\\.+\.exe)$")]
    private static partial Regex ImplicitIdRegex();

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    // --- Watching --------------------------------------------------------------------------------------------------

    public void StartWatching()
    {
        lock (_gate)
        {
            if (_stop is not null)
            {
                return;
            }

            _stop = new ManualResetEvent(false);
            _debounce = new Timer(_ => PinsChanged?.Invoke(this, EventArgs.Empty));
            _registryThread = new Thread(WatchRegistry) { IsBackground = true, Name = "SuperBar Taskband watcher" };
            _registryThread.Start(_stop);

            var folder = PinnedFolder;
            if (Directory.Exists(folder))
            {
                _folderWatcher = new FileSystemWatcher(folder, "*.lnk")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                };
                _folderWatcher.Created += OnFolderChanged;
                _folderWatcher.Deleted += OnFolderChanged;
                _folderWatcher.Changed += OnFolderChanged;
                _folderWatcher.Renamed += OnFolderChanged;
                _folderWatcher.EnableRaisingEvents = true;
            }
            else
            {
                _logger.LogInformation("The pinned shortcuts folder does not exist yet; watching the Taskband registry key only");
            }

            _logger.LogInformation("Watching the Windows taskbar pins for changes");
        }
    }

    public void StopWatching()
    {
        lock (_gate)
        {
            if (_stop is null)
            {
                return;
            }

            _stop.Set();
            _registryThread = null; // it sees the stop event and exits; it owns and disposes the event
            _stop = null;
            if (_folderWatcher is not null)
            {
                _folderWatcher.EnableRaisingEvents = false;
                _folderWatcher.Dispose();
                _folderWatcher = null;
            }

            _debounce?.Dispose();
            _debounce = null;
            _logger.LogInformation("Stopped watching the Windows taskbar pins");
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) => Signal($"pinned shortcuts folder ({e.ChangeType} {e.Name})");

    private void Signal(string source)
    {
        lock (_gate)
        {
            if (_debounce is null)
            {
                return;
            }

            _logger.LogDebug("Windows taskbar pins changed: {Source}", source);
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// RegNotifyChangeKeyValue on a thread of its own: the notification belongs to the thread that asked for it. Explorer
    /// rewrites the key for reasons other than pins, so only a different <c>Favorites</c> value counts as a change.
    /// </summary>
    private void WatchRegistry(object? state)
    {
        var stop = (ManualResetEvent)state!;
        try
        {
            WatchRegistry(stop);
        }
        catch (Exception ex)
        {
            // The stop event stays alive: StopWatching may still set it.
            _logger.LogWarning(ex, "The Taskband registry watcher stopped");
            return;
        }

        stop.Dispose(); // only reached once StopWatching set it and let go of it
    }

    private void WatchRegistry(ManualResetEvent stop)
    {
        using var changed = new AutoResetEvent(false);
        var last = ReadFavorites();
        while (!stop.WaitOne(0))
        {
            using (var key = Registry.CurrentUser.OpenSubKey(TaskbandKeyPath))
            {
                while (key is not null)
                {
                    var error = ShellInterop.RegNotifyChangeKeyValue(key.Handle, false,
                        ShellInterop.REG_NOTIFY_CHANGE_LAST_SET | ShellInterop.REG_NOTIFY_CHANGE_NAME, changed.SafeWaitHandle, true);
                    if (error != ShellInterop.ERROR_SUCCESS)
                    {
                        _logger.LogWarning("Cannot watch the Taskband registry key (error {Error}); retrying", error);
                        break;
                    }

                    if (WaitHandle.WaitAny([changed, stop]) == 1)
                    {
                        return;
                    }

                    var current = ReadFavorites();
                    if (current is null != last is null || (current is not null && !current.AsSpan().SequenceEqual(last)))
                    {
                        last = current;
                        Signal("Taskband registry value");
                    }
                }
            }

            // The key does not exist yet (a brand-new profile) or could not be watched: try again shortly.
            stop.WaitOne(RegistryRetry);
        }
    }

    public void Dispose() => StopWatching();
}

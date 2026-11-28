using System.Text.RegularExpressions;
using SuperBar.Core.Settings;

namespace SuperBar.Core.Shell;

/// <summary>
/// Decides which pin a running window belongs to, and whether two pins are the same app, the way the Windows taskbar
/// groups them: by explicit AppUserModelID when both sides have one, otherwise by executable path.
/// </summary>
public static partial class PinMatching
{
    /// <summary>The prefix of a packaged app's path: <c>shell:AppsFolder\&lt;AppUserModelID&gt;</c>.</summary>
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>The path SuperBar pins a packaged app as.</summary>
    public static string AppsFolderPath(string appUserModelId) => AppsFolderPrefix + appUserModelId;

    /// <summary>The AppUserModelID of a <c>shell:AppsFolder\…</c> path, or null.</summary>
    public static string? AppsFolderId(string? path) =>
        path is not null && path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) && path.Length > AppsFolderPrefix.Length
            ? path[AppsFolderPrefix.Length..]
            : null;

    /// <summary>Packaged apps' ids are "PackageFamilyName!AppId"; desktop apps' explicit ids never need the '!'.</summary>
    public static bool IsPackagedAppId(string? appUserModelId) =>
        !string.IsNullOrEmpty(appUserModelId) && appUserModelId.Contains('!');

    /// <summary>
    /// How to start a pin, as the Windows taskbar does: through its shortcut while it exists; else a pin known only by
    /// its id (a packaged app, or a desktop app pinned from the Start menu's list) through <c>shell:AppsFolder</c>;
    /// else its executable.
    /// </summary>
    public static AppLaunchTarget LaunchTarget(PinnedApp pin)
    {
        var appId = string.IsNullOrEmpty(pin.AppUserModelId) ? AppsFolderId(pin.Path) : pin.AppUserModelId;
        var packagedId = IsPackagedAppId(appId) ? appId : null;
        var path = string.IsNullOrEmpty(pin.ShortcutPath) && appId is not null && (packagedId is not null || AppsFolderId(pin.Path) is not null || pin.PinnedInWindows)
            ? AppsFolderPath(appId)
            : pin.Path;
        return new AppLaunchTarget(path, pin.Arguments, pin.WorkingDirectory, pin.ShortcutPath, packagedId);
    }

    /// <summary>
    /// The index of the pin a window belongs to, or -1.
    /// <list type="number">
    /// <item>A window with an explicit AppUserModelID joins the pin with the same id.</item>
    /// <item>Otherwise it joins the pin of its executable, unless both have explicit ids (and they differ): two browser
    /// profiles run the same executable but are separate apps on the Windows taskbar.</item>
    /// </list>
    /// </summary>
    public static int FindPin(IReadOnlyList<PinnedApp> pins, string? windowAppId, string? processPath)
    {
        if (!string.IsNullOrEmpty(windowAppId))
        {
            for (var i = 0; i < pins.Count; i++)
            {
                if (string.Equals(pins[i].AppUserModelId, windowAppId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (string.IsNullOrEmpty(processPath))
        {
            return -1;
        }

        for (var i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (!string.IsNullOrEmpty(windowAppId) && !string.IsNullOrEmpty(pin.AppUserModelId))
            {
                continue;
            }

            if (SamePath(pin.Path, processPath) || StartsProcess(pin, processPath))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True when two pins are the same app: their explicit ids decide when both have one, else their executables.</summary>
    public static bool SameApp(PinnedApp a, PinnedApp b)
    {
        if (!string.IsNullOrEmpty(a.AppUserModelId) && !string.IsNullOrEmpty(b.AppUserModelId))
        {
            return string.Equals(a.AppUserModelId, b.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        }

        return SamePath(a.Path, b.Path) || StartsProcess(a, b.Path) || StartsProcess(b, a.Path);
    }

    public static bool SamePath(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Self-updating apps start through a stub that runs the real executable from a versioned folder below it, so the
    /// pin and the windows have different paths:
    /// <list type="bullet">
    /// <item>Squirrel (Discord, Slack…): <c>&lt;root&gt;\Update.exe --processStart App.exe</c> runs <c>&lt;root&gt;\app-1.2.3\App.exe</c>.</item>
    /// <item>A launcher of the same name (Gemini…): <c>&lt;root&gt;\App.exe</c> runs <c>&lt;root&gt;\app-1.2.3\App.exe</c>.</item>
    /// </list>
    /// </summary>
    internal static bool StartsProcess(PinnedApp pin, string? processPath)
    {
        if (string.IsNullOrEmpty(processPath) || string.IsNullOrEmpty(pin.Path) || AppsFolderId(pin.Path) is not null)
        {
            return false;
        }

        var exe = Path.GetFileName(pin.Path);
        if (pin.Arguments is { } arguments && arguments.Contains("--processStart", StringComparison.OrdinalIgnoreCase)
            && ProcessStartRegex().Match(arguments) is { Success: true } match)
        {
            exe = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;
        }

        if (!string.Equals(Path.GetFileName(processPath), exe, StringComparison.OrdinalIgnoreCase)
            || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var root = Path.GetDirectoryName(Normalize(pin.Path));
        return !string.IsNullOrEmpty(root) && Path.IsPathFullyQualified(root)
            && Normalize(processPath).StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // Runs for every window on every bar update: only allocate when there is something to normalize.
    private static string Normalize(string path)
    {
        if (path.Contains('%'))
        {
            path = Environment.ExpandEnvironmentVariables(path);
        }

        if (path.Length > 0 && (path[0] == ' ' || path[0] == '"' || path[^1] == ' ' || path[^1] == '"'))
        {
            path = path.Trim().Trim('"');
        }

        return path.Contains('/') ? path.Replace('/', '\\') : path;
    }

    [GeneratedRegex("""--processStart(?:=|\s+)(?:"(?<quoted>[^"]+)"|(?<bare>\S+))""", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStartRegex();
}

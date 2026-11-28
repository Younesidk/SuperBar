using System.Diagnostics;

namespace SuperBar.Core.Shell;

/// <summary>How an executable is named and whether it can be pinned; shared by the bar and the Control Center.</summary>
public static class AppIdentity
{
    /// <summary>The executable's description ("Notepad", "JetBrains Rider") rather than its file name.</summary>
    public static string FriendlyName(string path, string fallback)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
            return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>
    /// False for hosts that cannot be launched to start the app they show: Store (UWP) apps run inside
    /// ApplicationFrameHost.exe, so pinning that would not bring the app back. Also false for an empty path.
    /// </summary>
    public static bool IsPinnable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
}

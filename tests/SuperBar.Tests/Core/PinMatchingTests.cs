using SuperBar.Core.Layout;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Tests.Core;

public class PinMatchingTests
{
    private static readonly PinnedApp Explorer = new() { DisplayName = "File Explorer", Path = @"C:\WINDOWS\explorer.exe", AppUserModelId = "Microsoft.Windows.Explorer" };
    private static readonly PinnedApp Terminal = new() { DisplayName = "Terminal", Path = @"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", AppUserModelId = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" };
    private static readonly PinnedApp Discord = new() { DisplayName = "Discord", Path = @"C:\Users\u\AppData\Local\Discord\Update.exe", Arguments = "--processStart Discord.exe", AppUserModelId = "com.squirrel.Discord.Discord" };
    private static readonly PinnedApp Gemini = new() { DisplayName = "Gemini", Path = @"C:\Users\u\AppData\Local\Google\Gemini\Gemini.exe", AppUserModelId = "com.google.gemini.windows" };
    private static readonly PinnedApp Browser = new() { DisplayName = "Aside", Path = @"C:\Program Files\Aside\Application\Aside.exe", AppUserModelId = "Aside" };
    private static readonly PinnedApp WebApp = new() { DisplayName = "Nexus", Path = @"C:\Program Files\Aside\Application\aside_proxy.exe", AppUserModelId = "Aside._crx_jfljafihf" };
    private static readonly PinnedApp Rider = new() { DisplayName = "Rider", Path = @"C:\Users\u\AppData\Local\Programs\Rider\bin\rider64.exe" };

    private static readonly IReadOnlyList<PinnedApp> Pins = [Explorer, Terminal, Discord, Gemini, Browser, WebApp, Rider];

    private static string? Match(string? appId, string? path) =>
        PinMatching.FindPin(Pins, appId, path) is var i and >= 0 ? Pins[i].DisplayName : null;

    [Fact]
    public void A_packaged_window_joins_its_pin_by_app_id() =>
        Assert.Equal("Terminal", Match("microsoft.windowsterminal_8wekyb3d8bbwe!app", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.0\WindowsTerminal.exe"));

    [Fact]
    public void A_window_without_app_id_joins_by_executable() =>
        Assert.Equal("Rider", Match(null, @"c:\users\u\appdata\local\programs\rider\bin\RIDER64.exe"));

    [Fact]
    public void Explorer_windows_join_the_file_explorer_pin() =>
        Assert.Equal("File Explorer", Match(null, @"C:\Windows\explorer.exe".Replace("Windows", "WINDOWS")));

    [Fact]
    public void Squirrel_apps_join_their_update_exe_pin() =>
        Assert.Equal("Discord", Match(null, @"C:\Users\u\AppData\Local\Discord\app-1.0.9219\Discord.exe"));

    [Fact]
    public void Apps_started_by_a_launcher_of_the_same_name_join_their_pin() =>
        Assert.Equal("Gemini", Match(null, @"C:\Users\u\AppData\Local\Google\Gemini\app-1.21.1\Gemini.exe"));

    [Fact]
    public void A_web_app_window_joins_its_own_pin_not_the_browser()
    {
        Assert.Equal("Nexus", Match("Aside._crx_jfljafihf", @"C:\Program Files\Aside\Application\Aside.exe"));
        Assert.Equal("Aside", Match("Aside", @"C:\Program Files\Aside\Application\Aside.exe"));
    }

    [Fact]
    public void A_window_with_another_explicit_id_is_a_separate_app() =>
        Assert.Null(Match("Aside.Profile2", @"C:\Program Files\Aside\Application\Aside.exe"));

    [Fact]
    public void Unrelated_windows_match_nothing()
    {
        Assert.Null(Match(null, @"C:\Other\Discord.exe"));
        Assert.Null(Match(null, null));
    }

    [Fact]
    public void A_local_pin_of_the_real_executable_is_the_same_app_as_the_squirrel_pin() =>
        Assert.True(PinMatching.SameApp(Discord, new PinnedApp { Path = @"C:\Users\u\AppData\Local\Discord\app-1.0.9219\Discord.exe" }));

    [Fact]
    public void Pins_are_keyed_by_app_id_when_they_have_one()
    {
        Assert.Equal("aumid:aside", AppOrdering.PinKey(Browser));
        Assert.Equal(@"c:\users\u\appdata\local\programs\rider\bin\rider64.exe", AppOrdering.PinKey(Rider));
    }

    [Fact]
    public void Launch_targets_follow_the_windows_taskbar()
    {
        var packaged = PinMatching.LaunchTarget(Terminal);
        Assert.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", packaged.PackagedAppId);
        Assert.Equal(Terminal.Path, packaged.Path);

        var shortcut = PinMatching.LaunchTarget(new PinnedApp { Path = @"C:\a.exe", ShortcutPath = @"C:\Pinned\a.lnk", Arguments = "-x", AppUserModelId = "A" });
        Assert.Equal(@"C:\Pinned\a.lnk", shortcut.ShortcutPath);
        Assert.Equal(@"C:\a.exe", shortcut.Path);
        Assert.Null(shortcut.PackagedAppId);

        // Pinned from the Start menu's list by id, no shortcut: started through shell:AppsFolder.
        var byId = PinMatching.LaunchTarget(new PinnedApp { Path = @"C:\WINDOWS\explorer.exe", AppUserModelId = "Microsoft.Windows.Explorer", PinnedInWindows = true });
        Assert.Equal(@"shell:AppsFolder\Microsoft.Windows.Explorer", byId.Path);

        var plain = PinMatching.LaunchTarget(Rider);
        Assert.Equal(Rider.Path, plain.Path);
        Assert.Null(plain.ShortcutPath);
    }

    [Fact]
    public void Manual_order_follows_a_new_pin_order_and_keeps_running_apps_in_place()
    {
        // Pins a, b, c; x is a running app the user dragged between them.
        var manual = new[] { "a", "x", "b", "c" };

        Assert.Equal(["c", "x", "a", "b"], AppOrdering.FollowPinOrder(manual, ["c", "a", "b"]));
        Assert.Equal(["a", "n", "x", "b", "c"], AppOrdering.FollowPinOrder(manual, ["a", "n", "b", "c"]));
        Assert.Equal(["a", "x", "b", "c"], AppOrdering.FollowPinOrder(manual, ["a", "b", "c"]));
        Assert.Empty(AppOrdering.FollowPinOrder([], ["a"]));
    }
}

namespace SuperBar.Core.Settings;

public static class DefaultSettings
{
    public const string SystemResourcesWidgetType = "superbar.system-resources";

    public static SuperBarSettings Create()
    {
        var resources = new WidgetInstanceSettings
        {
            InstanceId = "system-resources-1",
            TypeId = SystemResourcesWidgetType,
        };

        var settings = new SuperBarSettings
        {
            Widgets = { resources },
            Layout =
            {
                new SectionPlacement { Id = "start", Kind = SectionKind.Start, Zone = TaskbarZone.Left },
                new SectionPlacement { Id = "apps", Kind = SectionKind.Applications, Zone = TaskbarZone.Left },
                new SectionPlacement { Id = "widget-resources", Kind = SectionKind.Widget, Zone = TaskbarZone.Right, WidgetInstanceId = resources.InstanceId },
                new SectionPlacement { Id = "tray", Kind = SectionKind.SystemTray, Zone = TaskbarZone.Right },
                new SectionPlacement { Id = "clock", Kind = SectionKind.Clock, Zone = TaskbarZone.Right },
            },
        };

        settings.PinnedApps.AddRange(DefaultPins());
        return settings;
    }

    private static IEnumerable<PinnedApp> DefaultPins()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new (string Name, string Path)[]
        {
            ("File Explorer", System.IO.Path.Combine(windows, "explorer.exe")),
            ("Microsoft Edge", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe")),
            ("Task Manager", System.IO.Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")),
        };

        foreach (var (name, path) in candidates)
        {
            if (File.Exists(path))
            {
                yield return new PinnedApp { Id = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), DisplayName = name, Path = path };
            }
        }
    }
}

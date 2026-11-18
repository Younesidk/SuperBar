using SuperBar.Core.Settings;

namespace SuperBar.Tests.Core;

public class SettingsNormalizerTests
{
    [Fact]
    public void Defaults_are_already_normal_and_contain_the_core_sections()
    {
        var s = SettingsNormalizer.Normalize(DefaultSettings.Create());

        Assert.Contains(s.Layout, p => p.Kind == SectionKind.Start);
        Assert.Contains(s.Layout, p => p.Kind == SectionKind.Applications);
        Assert.Contains(s.Layout, p => p.Kind == SectionKind.Clock);
        Assert.Contains(s.Layout, p => p.Kind == SectionKind.Widget && p.WidgetInstanceId == "system-resources-1");
    }

    [Theory]
    [InlineData(5, AppearanceSettings.MinHeight)]
    [InlineData(500, AppearanceSettings.MaxHeight)]
    [InlineData(double.NaN, 52)]
    [InlineData(48, 48)]
    public void Height_is_clamped(double input, double expected)
    {
        var s = DefaultSettings.Create();
        s.Appearance.Height = input;

        Assert.Equal(expected, SettingsNormalizer.Normalize(s).Appearance.Height);
    }

    [Fact]
    public void Widget_sections_referencing_missing_instances_are_removed()
    {
        var s = DefaultSettings.Create();
        s.Layout.Add(new SectionPlacement { Kind = SectionKind.Widget, Zone = TaskbarZone.Center, WidgetInstanceId = "does-not-exist" });

        var n = SettingsNormalizer.Normalize(s);

        Assert.DoesNotContain(n.Layout, p => p.WidgetInstanceId == "does-not-exist");
    }

    [Fact]
    public void Missing_start_and_applications_sections_are_restored_and_duplicates_dropped()
    {
        var s = new SuperBarSettings
        {
            Layout =
            {
                new SectionPlacement { Id = "c1", Kind = SectionKind.Clock, Zone = TaskbarZone.Right },
                new SectionPlacement { Id = "c2", Kind = SectionKind.Clock, Zone = TaskbarZone.Left },
            },
        };

        var n = SettingsNormalizer.Normalize(s);

        Assert.Equal(SectionKind.Start, n.Layout[0].Kind);
        Assert.Equal(SectionKind.Applications, n.Layout[1].Kind);
        Assert.Single(n.Layout, p => p.Kind == SectionKind.Clock);
    }

    [Fact]
    public void Null_collections_from_a_damaged_file_are_repaired()
    {
        var s = new SuperBarSettings { Appearance = null!, Layout = null!, Widgets = null!, PinnedApps = null! };

        var n = SettingsNormalizer.Normalize(s);

        Assert.NotNull(n.Appearance);
        Assert.NotEmpty(n.Layout);
        Assert.NotNull(n.Widgets);
        Assert.NotNull(n.PinnedApps);
    }
}

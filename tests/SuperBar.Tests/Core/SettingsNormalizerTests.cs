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
        Assert.Contains(s.Layout, p => p.Kind == SectionKind.Widget && p.WidgetInstanceId == "system-resources-1");
        Assert.Contains(s.Layout, p => p.Kind == SectionKind.Widget && p.WidgetInstanceId == DefaultSettings.ClockWidgetInstanceId);
        Assert.Contains(s.Widgets, w => w.TypeId == DefaultSettings.ClockWidgetType);
        Assert.DoesNotContain(s.Layout, p => p.Kind == SectionKind.Clock);
    }

    [Fact]
    public void A_legacy_clock_section_becomes_a_clock_widget_in_the_same_place()
    {
        var s = new SuperBarSettings
        {
            Widgets = { new WidgetInstanceSettings { InstanceId = "res", TypeId = DefaultSettings.SystemResourcesWidgetType } },
            Layout =
            {
                new SectionPlacement { Id = "start", Kind = SectionKind.Start, Zone = TaskbarZone.Left },
                new SectionPlacement { Id = "apps", Kind = SectionKind.Applications, Zone = TaskbarZone.Left },
                new SectionPlacement { Id = "my-clock", Kind = SectionKind.Clock, Zone = TaskbarZone.Center },
                new SectionPlacement { Id = "w", Kind = SectionKind.Widget, Zone = TaskbarZone.Right, WidgetInstanceId = "res" },
            },
        };

        var n = SettingsNormalizer.Normalize(s);

        var clock = Assert.Single(n.Widgets, w => w.TypeId == DefaultSettings.ClockWidgetType);
        Assert.True(clock.Enabled);
        Assert.Equal(["start", "apps", "my-clock", "w"], n.Layout.Select(p => p.Id));
        var placement = n.Layout[2];
        Assert.Equal(SectionKind.Widget, placement.Kind);
        Assert.Equal(TaskbarZone.Center, placement.Zone);
        Assert.Equal(clock.InstanceId, placement.WidgetInstanceId);
        Assert.Equal(SuperBarSettings.CurrentSchemaVersion, n.SchemaVersion);
    }

    [Fact]
    public void Migrating_the_clock_section_is_idempotent_and_reuses_an_existing_clock_widget()
    {
        var s = new SuperBarSettings
        {
            Widgets = { new WidgetInstanceSettings { InstanceId = "existing-clock", TypeId = DefaultSettings.ClockWidgetType } },
            Layout =
            {
                new SectionPlacement { Id = "legacy", Kind = SectionKind.Clock, Zone = TaskbarZone.Right },
            },
        };

        var once = SettingsNormalizer.Normalize(s);
        var twice = SettingsNormalizer.Normalize(once);

        Assert.Single(twice.Widgets, w => w.TypeId == DefaultSettings.ClockWidgetType);
        var placement = Assert.Single(twice.Layout, p => p.Kind == SectionKind.Widget);
        Assert.Equal("existing-clock", placement.WidgetInstanceId);
        Assert.DoesNotContain(twice.Layout, p => p.Kind == SectionKind.Clock);
    }

    [Fact]
    public void A_legacy_clock_section_is_dropped_when_the_clock_widget_is_already_placed()
    {
        var s = DefaultSettings.Create();
        s.Layout.Add(new SectionPlacement { Id = "legacy", Kind = SectionKind.Clock, Zone = TaskbarZone.Left });

        var n = SettingsNormalizer.Normalize(s);

        Assert.Single(n.Layout, p => p.WidgetInstanceId == DefaultSettings.ClockWidgetInstanceId);
        Assert.DoesNotContain(n.Layout, p => p.Id == "legacy");
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
        Assert.Single(n.Layout, p => p.Kind == SectionKind.Widget);
        Assert.DoesNotContain(n.Layout, p => p.Kind == SectionKind.Clock);
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

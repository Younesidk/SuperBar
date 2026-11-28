using SuperBar.Core.Settings;

namespace SuperBar.Tests.Core;

public class WidgetInstancesTests
{
    private const string Resources = DefaultSettings.SystemResourcesWidgetType;
    private const string Clock = DefaultSettings.ClockWidgetType;

    [Fact]
    public void A_right_zone_widget_joins_the_other_widgets_before_the_system_section()
    {
        var s = DefaultSettings.Create(); // start, apps | resources, tray, clock

        var id = WidgetInstances.Add(s, Resources, TaskbarZone.Right, allowMultiple: true);

        Assert.Equal("system-resources-2", id);
        Assert.Equal(["start", "apps", "widget-resources", "widget-system-resources-2", "tray", "clock"], s.Layout.Select(p => p.Id));
        var placement = s.Layout.Single(p => p.WidgetInstanceId == id);
        Assert.Equal(SectionKind.Widget, placement.Kind);
        Assert.Equal(TaskbarZone.Right, placement.Zone);
        Assert.True(s.Widgets.Single(w => w.InstanceId == id).Enabled);
    }

    [Fact]
    public void Other_zones_get_the_widget_at_their_end_or_at_the_end_of_the_layout()
    {
        var s = DefaultSettings.Create();

        WidgetInstances.Add(s, "x.left", TaskbarZone.Left, allowMultiple: true);
        WidgetInstances.Add(s, "x.center", TaskbarZone.Center, allowMultiple: true);

        Assert.Equal("widget-left-1", s.Layout[2].Id);           // after start and apps
        Assert.Equal("widget-center-1", s.Layout[^1].Id);        // the center zone was empty
        Assert.Equal(TaskbarZone.Center, s.Layout[^1].Zone);
    }

    [Fact]
    public void A_single_instance_type_cannot_be_added_twice()
    {
        var s = DefaultSettings.Create(); // already has a clock

        Assert.Null(WidgetInstances.Add(s, Clock, TaskbarZone.Right, allowMultiple: false));
        Assert.Single(s.Widgets, w => w.TypeId == Clock);
    }

    [Fact]
    public void Removing_an_instance_removes_its_placement_and_frees_its_slot()
    {
        var s = DefaultSettings.Create();

        Assert.True(WidgetInstances.Remove(s, DefaultSettings.ClockWidgetInstanceId));
        Assert.DoesNotContain(s.Widgets, w => w.TypeId == Clock);
        Assert.DoesNotContain(s.Layout, p => p.WidgetInstanceId == DefaultSettings.ClockWidgetInstanceId);
        Assert.False(WidgetInstances.Remove(s, "nope"));

        // Removed single-instance types can come back.
        Assert.Equal("clock-1", WidgetInstances.Add(s, Clock, TaskbarZone.Right, allowMultiple: false));
    }

    [Fact]
    public void Added_instances_survive_normalization()
    {
        var s = DefaultSettings.Create();
        var id = WidgetInstances.Add(s, Resources, TaskbarZone.Right, allowMultiple: true);

        var n = SettingsNormalizer.Normalize(s);

        Assert.Contains(n.Layout, p => p.WidgetInstanceId == id);
    }
}

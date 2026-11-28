using Microsoft.Extensions.Logging.Abstractions;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Infrastructure.Persistence;

namespace SuperBar.Tests.Infrastructure;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SuperBar.Tests", Guid.NewGuid().ToString("N"));

    public SettingsPersistenceTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "settings.db");

    private SettingsService CreateService() =>
        new(new SqliteSettingsStore(DatabasePath), NullLogger<SettingsService>.Instance);

    [Fact]
    public void First_start_writes_defaults()
    {
        var service = CreateService();

        Assert.Equal(52, service.Current.Appearance.Height);
        Assert.NotNull(new SqliteSettingsStore(DatabasePath).Read("layout"));
    }

    [Fact]
    public void Settings_saved_before_the_clock_became_a_widget_still_load()
    {
        CreateService(); // creates the database
        var store = new SqliteSettingsStore(DatabasePath);
        store.Write("widgets", """[{"instanceId":"system-resources-1","typeId":"superbar.system-resources","enabled":true,"width":180,"options":{}}]""");
        store.Write("layout", """
            [
              {"id":"start","kind":"Start","zone":"Left"},
              {"id":"apps","kind":"Applications","zone":"Center"},
              {"id":"clock","kind":"Clock","zone":"Left"},
              {"id":"widget-resources","kind":"Widget","zone":"Right","widgetInstanceId":"system-resources-1"}
            ]
            """);

        var loaded = CreateService().Current;

        // The user's layout survived (it was not reset to the defaults) and the clock is a widget now.
        Assert.Equal(["start", "apps", "clock", "widget-resources"], loaded.Layout.Select(p => p.Id));
        Assert.Equal(TaskbarZone.Center, loaded.Layout[1].Zone);
        var clock = loaded.Layout[2];
        Assert.Equal(SectionKind.Widget, clock.Kind);
        Assert.Equal(TaskbarZone.Left, clock.Zone);
        Assert.Equal(DefaultSettings.ClockWidgetType, loaded.Widgets.Single(w => w.InstanceId == clock.WidgetInstanceId).TypeId);
        Assert.Equal(180, loaded.Widgets.Single(w => w.TypeId == DefaultSettings.SystemResourcesWidgetType).Width);
    }

    [Fact]
    public void Widget_options_saved_as_strings_by_schema_1_still_load()
    {
        CreateService();
        var store = new SqliteSettingsStore(DatabasePath);
        store.Write("widgets", """[{"instanceId":"clock-1","typeId":"superbar.clock","enabled":true,"options":{"showSeconds":"true","TimeFormat":"TwentyFourHour"}}]""");

        var loaded = CreateService().Current.Widgets.Single(w => w.InstanceId == "clock-1");
        var typed = SuperBar.Widgets.WidgetSettingsBinder.Bind<SuperBar.Widgets.BuiltIn.ClockSettings>(loaded.Options);

        Assert.True(typed.ShowSeconds);
        Assert.Equal(SuperBar.Widgets.BuiltIn.ClockTimeFormat.TwentyFourHour, typed.TimeFormat);
        Assert.True(typed.ShowDate); // missing: default
    }

    [Fact]
    public void Widget_options_are_saved_as_typed_json()
    {
        CreateService().Update(s => s.Widgets[0].Options["showSeconds"] = true);

        var row = new SqliteSettingsStore(DatabasePath).Read("widgets")!;

        Assert.Contains("\"showSeconds\":true", row);
    }

    [Fact]
    public void Updates_persist_across_instances()
    {
        CreateService().Update(s =>
        {
            s.Appearance.Height = 60;
            s.Behavior.HideNativeTaskbar = false;
            s.Widgets[0].Width = 200;
        });

        var reloaded = CreateService().Current;

        Assert.Equal(60, reloaded.Appearance.Height);
        Assert.False(reloaded.Behavior.HideNativeTaskbar);
        Assert.Equal(200, reloaded.Widgets[0].Width);
    }

    [Fact]
    public void Update_publishes_a_new_snapshot_and_never_mutates_the_old_one()
    {
        var service = CreateService();
        var before = service.Current;
        SettingsChangedEventArgs? raised = null;
        service.Changed += (_, e) => raised = e;

        service.Update(s => s.Appearance.Height = 70);

        Assert.NotNull(raised);
        Assert.Same(before, raised.Previous);
        Assert.Equal(52, before.Appearance.Height);
        Assert.Equal(70, service.Current.Appearance.Height);
    }

    [Fact]
    public void Invalid_values_are_normalized_before_saving()
    {
        var service = CreateService();

        service.Update(s => s.Appearance.Height = 1000);

        Assert.Equal(AppearanceSettings.MaxHeight, CreateService().Current.Appearance.Height);
    }

    [Fact]
    public void A_corrupt_area_falls_back_to_defaults_without_losing_other_areas()
    {
        CreateService().Update(s => s.Appearance.Height = 64);
        new SqliteSettingsStore(DatabasePath).Write("layout", "{not json");

        var reloaded = CreateService().Current;

        Assert.Equal(64, reloaded.Appearance.Height);
        Assert.Contains(reloaded.Layout, p => p.Kind == SectionKind.Start);
    }

    [Fact]
    public void Recovery_record_round_trips_and_clears()
    {
        var store = new JsonTaskbarRecoveryStore(Path.Combine(_directory, "recovery.json"));
        var record = new NativeTaskbarRecoveryRecord(true, true, 2, 1234, DateTimeOffset.UtcNow);

        store.Save(record);
        Assert.Equal(record, store.Load());

        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void A_damaged_recovery_record_is_treated_as_taskbar_hidden()
    {
        var path = Path.Combine(_directory, "recovery.json");
        File.WriteAllText(path, "garbage");

        var loaded = new JsonTaskbarRecoveryStore(path).Load();

        Assert.NotNull(loaded);
        Assert.True(loaded.TaskbarHidden);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

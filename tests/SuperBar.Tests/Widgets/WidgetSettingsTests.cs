using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Infrastructure.Persistence;
using SuperBar.Widgets;

namespace SuperBar.Tests.Widgets;

/// <summary>A settings store in memory, with a write log.</summary>
internal sealed class MemorySettingsStore : ISettingsStore
{
    public Dictionary<string, string> Rows { get; } = new();

    public List<string> Writes { get; } = new();

    public string? Read(string key) => Rows.TryGetValue(key, out var value) ? value : null;

    public void Write(string key, string json)
    {
        Rows[key] = json;
        Writes.Add(key);
    }

    public void Delete(string key) => Rows.Remove(key);
}

public class WidgetSettingsSchemaTests
{
    private static WidgetSettingsSchema Schema => WidgetSettingsSchema.For(typeof(FormSettings));

    [Fact]
    public void Fields_come_from_setting_attributes_in_order()
    {
        Assert.Equal(["enabled", "minutes", "volume", "mode", "title"], Schema.Fields.Select(f => f.Key));
        Assert.DoesNotContain(Schema.Fields, f => f.Key == "hidden");
    }

    [Fact]
    public void Each_field_describes_its_editor()
    {
        var f = Schema.Fields.ToDictionary(x => x.Key);

        Assert.Equal(SettingKind.Toggle, f["enabled"].Kind);
        Assert.Equal("Enabled", f["enabled"].Label);
        Assert.Equal("Turns it on.", f["enabled"].Description);
        Assert.Equal(true, f["enabled"].DefaultValue);

        var minutes = f["minutes"];
        Assert.Equal(SettingKind.Number, minutes.Kind);
        Assert.True(minutes.IsInteger);
        Assert.True(minutes.HasRange);
        Assert.Equal(5, minutes.Minimum);
        Assert.Equal(90, minutes.Maximum);
        Assert.Equal("min", minutes.Unit);
        Assert.Equal(25, minutes.DefaultValue);

        Assert.False(f["volume"].HasRange); // only a minimum: number box, not a slider
        Assert.False(f["volume"].IsInteger);

        var mode = f["mode"];
        Assert.Equal(SettingKind.Choice, mode.Kind);
        Assert.Equal(["Compact view", "Detailed"], mode.Choices.Select(c => c.Label)); // [Description], else humanized
        Assert.Equal(Mode.Detailed, mode.Choices[1].Value);

        Assert.Equal(SettingKind.Text, f["title"].Kind);
    }

    [Fact]
    public void Values_are_read_typed_from_new_and_legacy_options()
    {
        var f = Schema.Fields.ToDictionary(x => x.Key);
        var legacy = new JsonObject { ["Minutes"] = "40", ["enabled"] = "false", ["mode"] = "detailed" };

        Assert.Equal(40, Schema.Read(legacy, f["minutes"]));
        Assert.Equal(false, Schema.Read(legacy, f["enabled"]));
        Assert.Equal(Mode.Detailed, Schema.Read(legacy, f["mode"]));
        Assert.Equal("Focus", Schema.Read(legacy, f["title"]));         // missing: default
        Assert.Equal(25, Schema.Read(new JsonObject { ["minutes"] = "soon" }, f["minutes"])); // invalid: default
        Assert.Equal(90, Schema.Read(new JsonObject { ["minutes"] = 500 }, f["minutes"]));    // out of range: clamped
    }

    [Fact]
    public void Writes_store_typed_values_clamped_and_rounded()
    {
        var f = Schema.Fields.ToDictionary(x => x.Key);
        var options = new JsonObject { ["Minutes"] = "40", ["unknown"] = "kept" };

        Schema.Write(options, f["minutes"], 32.6);   // a slider value
        Schema.Write(options, f["enabled"], false);
        Schema.Write(options, f["mode"], Mode.Detailed);
        Schema.Write(options, f["volume"], -3.0);

        Assert.Equal("""{"unknown":"kept","minutes":33,"enabled":false,"mode":"Detailed","volume":0}""", options.ToJsonString());
    }

    [Fact]
    public void Settings_the_form_cannot_show_are_rejected_when_the_schema_is_built()
    {
        Assert.Throws<InvalidOperationException>(() => WidgetSettingsSchema.For(typeof(UnsupportedType)));
        Assert.Throws<InvalidOperationException>(() => WidgetSettingsSchema.For(typeof(ReadOnlySetting)));
        Assert.Throws<InvalidOperationException>(() => WidgetSettingsSchema.For(typeof(InvertedRange)));
    }

    [Fact]
    public void A_widget_descriptor_knows_its_settings_schema()
    {
        var descriptor = WidgetDescriptor.FromType(typeof(SuperBar.Widgets.BuiltIn.ClockWidget));

        Assert.Equal(typeof(SuperBar.Widgets.BuiltIn.ClockSettings), descriptor.SettingsType);
        Assert.Equal(["timeFormat", "showSeconds", "showDate"], descriptor.SettingsSchema!.Fields.Select(f => f.Key));
        Assert.Null(WidgetDescriptor.FromType(typeof(SuperBar.Widgets.BuiltIn.SystemResourcesWidget)).SettingsSchema);
    }

    [Theory]
    [InlineData("TwentyFourHour", "Twenty four hour")]
    [InlineData("Compact", "Compact")]
    [InlineData("ShowAMPM", "Show aMPM")]
    public void Enum_names_are_humanized(string name, string expected) =>
        Assert.Equal(expected, WidgetSettingsSchema.Humanize(name));

    internal enum Mode
    {
        [Description("Compact view")]
        Compact,
        Detailed,
    }

    internal sealed class FormSettings
    {
        [Setting("Title", Order = 5)]
        public string Title { get; set; } = "Focus";

        [Setting("Enabled", Description = "Turns it on.")]
        public bool Enabled { get; set; } = true;

        [Setting("Minutes", Minimum = 5, Maximum = 90, Unit = "min")]
        public int Minutes { get; set; } = 25;

        [Setting("Volume", Minimum = 0)]
        public double Volume { get; set; } = 0.5;

        [Setting("Mode")]
        public Mode Mode { get; set; }

        public string Hidden { get; set; } = "";
    }

    internal sealed class UnsupportedType
    {
        [Setting("Tags")]
        public List<string> Tags { get; set; } = new();
    }

    internal sealed class ReadOnlySetting
    {
        [Setting("Fixed")]
        public int Fixed => 1;
    }

    internal sealed class InvertedRange
    {
        [Setting("Bad", Minimum = 10, Maximum = 1)]
        public int Bad { get; set; }
    }
}

public class WidgetOptionsTests
{
    [Fact]
    public void Typed_settings_serialize_to_camel_case_typed_json()
    {
        var json = WidgetSettingsBinder.ToJson(new PomodoroSettings { Minutes = 50, Sound = false, Mode = PomodoroMode.Long });

        Assert.Equal("""{"minutes":50,"sound":false,"mode":"Long"}""", json.ToJsonString());
        Assert.Equal(50, WidgetSettingsBinder.Bind<PomodoroSettings>(json).Minutes);
    }

    [Fact]
    public void Merging_replaces_other_spellings_and_keeps_unknown_keys()
    {
        var stored = new JsonObject { ["Minutes"] = "40", ["fromNewerVersion"] = 1 };

        var merged = WidgetOptions.Merge(stored, new JsonObject { ["minutes"] = 50 });

        Assert.Equal("""{"fromNewerVersion":1,"minutes":50}""", merged.ToJsonString());
        Assert.Equal("""{"Minutes":"40","fromNewerVersion":1}""", stored.ToJsonString()); // input untouched
    }

    internal enum PomodoroMode
    {
        Short,
        Long,
    }

    internal sealed class PomodoroSettings
    {
        public int Minutes { get; set; } = 25;
        public bool Sound { get; set; } = true;
        public PomodoroMode Mode { get; set; }
    }
}

/// <summary>
/// Settings and state through a real <see cref="SettingsService"/> (over an in-memory store), wired to the host
/// the way the taskbar runtime does it: every published settings change is handed to <see cref="WidgetHost.Apply"/>.
/// </summary>
public sealed class WidgetPersistenceTests
{
    private readonly FakeUiDispatcher _ui = new();
    private readonly MemorySettingsStore _store = new();
    private readonly SettingsService _settings;
    private readonly List<SettingsChangedEventArgs> _changes = new();

    public WidgetPersistenceTests()
    {
        _settings = new SettingsService(_store, NullLogger<SettingsService>.Instance);
        _settings.Update(s =>
        {
            s.Widgets.Add(new WidgetInstanceSettings { InstanceId = "p1", TypeId = "test.pomodoro", Options = { ["Minutes"] = "40" } });
            s.Layout.Add(new SectionPlacement { Kind = SectionKind.Widget, Zone = TaskbarZone.Center, WidgetInstanceId = "p1" });
        });
        _settings.Changed += (_, e) => _changes.Add(e);
    }

    private WidgetHost Host(out Func<PomodoroWidget> widget)
    {
        PomodoroWidget? created = null;
        widget = () => created!;
        var descriptor = new WidgetDescriptor("test.pomodoro", "Pomodoro", (_, context) => created = new PomodoroWidget(context))
        {
            SettingsType = typeof(PomodoroSettings),
        };
        var services = new ServiceCollection()
            .AddSingleton<IUiDispatcher>(_ui)
            .AddSingleton<ISettingsService>(_settings)
            .AddSingleton<ISettingsStore>(_store)
            .BuildServiceProvider();
        var host = new WidgetHost(descriptor, Instance(), services, NullLoggerFactory.Instance);

        // What TaskbarViewModel does with every settings change.
        _settings.Changed += (_, e) => host.Apply(e.Current.Widgets.Single(w => w.InstanceId == "p1"));
        return host;
    }

    private WidgetInstanceSettings Instance() => _settings.Current.Widgets.Single(w => w.InstanceId == "p1");

    [Fact]
    public void A_widget_saves_its_settings_typed_without_being_recreated_or_notified_twice()
    {
        var created = 0;
        var host = Host(out var widget);
        host.StateChanged += (_, _) => created += host.State == WidgetState.Initializing ? 1 : 0;
        host.Activate();
        Assert.Equal(40, widget().Minutes); // the legacy string value, typed

        widget().Model.LongerFocus.Execute();

        Assert.Equal(45, widget().Minutes);
        Assert.Equal(["changed 40 -> 45"], widget().Changes); // once: the persisted change does not echo back
        Assert.Equal(1, created);
        Assert.Equal("""{"minutes":45,"sound":true}""", Instance().Options.ToJsonString());

        // And it is on disk, typed.
        var reloaded = new SettingsService(_store, NullLogger<SettingsService>.Instance).Current;
        Assert.Equal(45, (int)reloaded.Widgets.Single(w => w.InstanceId == "p1").Options["minutes"]!);
    }

    [Fact]
    public void An_edit_from_the_control_center_reaches_the_running_widget_live()
    {
        var host = Host(out var widget);
        host.Activate();
        var edit = new WidgetSettingsEditContext(_settings, "p1", host.Descriptor);
        var minutes = edit.Schema!.Fields.Single(f => f.Key == "minutes");

        edit.Set(minutes, 55.0);

        Assert.Equal(55, widget().Minutes);
        Assert.Equal(["changed 40 -> 55"], widget().Changes);
        Assert.Equal(55, edit.Get(minutes));
        Assert.Equal(WidgetState.Active, host.State);
    }

    [Fact]
    public void A_custom_editor_saves_typed_settings_and_keeps_keys_it_does_not_know()
    {
        _settings.Update(s => s.Widgets.Single(w => w.InstanceId == "p1").Options["extra"] = "kept");
        var host = Host(out var widget);
        host.Activate();
        var edit = new WidgetSettingsEditContext(_settings, "p1", host.Descriptor);

        Assert.Equal(40, edit.Load<PomodoroSettings>().Minutes);
        edit.Save<PomodoroSettings>(s => s.Sound = false);

        Assert.False(widget().Sound);
        Assert.Equal("kept", (string?)Instance().Options["extra"]);
        Assert.Equal(false, (bool?)Instance().Options["sound"]);
    }

    [Fact]
    public void Runtime_state_is_kept_in_its_own_row_and_never_publishes_a_settings_change()
    {
        var host = Host(out var widget);
        host.Activate();
        _changes.Clear();
        _store.Writes.Clear();

        widget().Model.CompleteSession.Execute();
        widget().Model.CompleteSession.Execute();

        Assert.Empty(_changes);                                     // no bar re-layout
        Assert.Equal(["widget-state:p1", "widget-state:p1"], _store.Writes); // nothing else was written
        Assert.Equal("""{"completedSessions":2}""", _store.Rows["widget-state:p1"]);
    }

    [Fact]
    public void Runtime_state_survives_a_restart_of_the_widget()
    {
        var host = Host(out var widget);
        host.Activate();
        widget().Model.CompleteSession.Execute();
        host.Dispose();

        var again = Host(out var widget2);
        again.Activate();

        Assert.Equal(1, widget2().CompletedAtStart);
    }

    [Fact]
    public void Corrupt_state_starts_fresh()
    {
        _store.Rows["widget-state:p1"] = "{ not json";
        var host = Host(out var widget);

        host.Activate();

        Assert.Equal(0, widget().CompletedAtStart);
        Assert.Equal(WidgetState.Active, host.State);
    }

    internal sealed class PomodoroSettings
    {
        [Setting("Focus length", Minimum = 5, Maximum = 90)]
        public int Minutes { get; set; } = 25;

        [Setting("Sound")]
        public bool Sound { get; set; } = true;
    }

    internal sealed class PomodoroState
    {
        public int CompletedSessions { get; set; }
    }

    internal sealed class PomodoroModel
    {
        public WidgetCommand LongerFocus { get; } = new();
        public WidgetCommand CompleteSession { get; } = new();
    }

    internal sealed class PomodoroWidget : WidgetBase<PomodoroSettings, PomodoroModel>
    {
        public PomodoroWidget(WidgetContext context)
            : base(context, new PomodoroModel())
        {
            On(Model.LongerFocus, () => SaveSettings(s => s.Minutes += 5));
            On(Model.CompleteSession, () =>
            {
                var state = Context.State.Load<PomodoroState>();
                state.CompletedSessions++;
                Context.State.Save(state);
            });
        }

        public int Minutes => Settings.Minutes;
        public bool Sound => Settings.Sound;
        public List<string> Changes { get; } = new();
        public int CompletedAtStart { get; private set; }

        protected override Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            CompletedAtStart = Context.State.Load<PomodoroState>().CompletedSessions;
            return Task.CompletedTask;
        }

        protected override void OnActivated(IWidgetScope scope)
        {
        }

        protected override void OnSettingsChanged(PomodoroSettings previous) =>
            Changes.Add($"changed {previous.Minutes} -> {Settings.Minutes}");
    }
}

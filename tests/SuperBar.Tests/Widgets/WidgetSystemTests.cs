using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBar.Core.Settings;
using SuperBar.Widgets;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Tests.Widgets;

public class WidgetDescriptorTests
{
    private static WidgetDescriptor Make(double def = 100, double min = 50, double max = 200) =>
        new("test.widget", "Test", (_, _) => new ScriptedWidget(), "", def, min, max);

    [Theory]
    [InlineData(null, 100)]
    [InlineData(10.0, 50)]
    [InlineData(999.0, 200)]
    [InlineData(120.0, 120)]
    public void Width_is_resolved_within_the_type_limits(double? requested, double expected) =>
        Assert.Equal(expected, Make().ResolveWidth(requested));

    [Fact]
    public void Inconsistent_width_ranges_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Make(def: 300));
        Assert.Throws<ArgumentException>(() => Make(min: 0));
        Assert.Throws<ArgumentException>(() => Make(min: 150, max: 100));
        Assert.Throws<ArgumentException>(() => Make(def: double.NaN));
    }

    [Fact]
    public void Registry_rejects_duplicate_type_ids()
    {
        Assert.Throws<InvalidOperationException>(() => new WidgetRegistry([Make(), Make()]));
    }
}

public class WidgetDiscoveryTests
{
    [Fact]
    public void Built_in_widgets_are_discovered_without_any_registration_code()
    {
        var registry = new ServiceCollection().AddSuperBarWidgets().BuildServiceProvider().GetRequiredService<IWidgetRegistry>();

        Assert.True(registry.TryGet(DefaultSettings.SystemResourcesWidgetType, out var resources));
        Assert.Equal(typeof(SystemResourcesWidget), resources.WidgetType);
        Assert.Equal(WidgetSizing.Fixed, resources.Sizing);
        Assert.Equal(156, resources.DefaultWidth);

        Assert.True(registry.TryGet(DefaultSettings.ClockWidgetType, out var clock));
        Assert.Equal(typeof(ClockWidget), clock.WidgetType);
        Assert.Equal(WidgetSizing.Auto, clock.Sizing);
        Assert.False(clock.ShowBackground);
        Assert.False(clock.AllowMultiple);
    }

    [Fact]
    public void Every_widget_in_the_default_settings_has_a_registered_type()
    {
        var registry = new ServiceCollection().AddSuperBarWidgets().BuildServiceProvider().GetRequiredService<IWidgetRegistry>();

        Assert.All(DefaultSettings.Create().Widgets, w => Assert.True(registry.TryGet(w.TypeId, out _), w.TypeId));
    }

    [Fact]
    public void A_widget_class_is_found_by_its_attribute_and_created_with_constructor_injection()
    {
        var dependency = new Dependency();
        var services = new ServiceCollection()
            .AddSingleton(dependency)
            .AddSingleton<IWidgetRegistry, WidgetRegistry>()
            .AddWidgetsFrom(typeof(WidgetDiscoveryTests).Assembly)
            .BuildServiceProvider();

        var descriptor = Assert.Single(services.GetRequiredService<IWidgetRegistry>().All);
        Assert.Equal("test.discovered", descriptor.TypeId);
        Assert.Equal("Discovered", descriptor.DisplayName);
        Assert.Equal("Found by attribute.", descriptor.Description);
        Assert.Equal(TaskbarZone.Center, descriptor.DefaultZone);
        Assert.Equal(64, descriptor.MinWidth);

        var context = new WidgetContext("d1", NullLogger.Instance);
        var widget = Assert.IsType<DiscoveredWidget>(descriptor.Factory(services, context));
        Assert.Same(dependency, widget.Dependency);
        Assert.Same(context, widget.ReceivedContext);
    }

    [Fact]
    public void A_class_without_the_attribute_is_not_a_widget_type()
    {
        Assert.Throws<ArgumentException>(() => WidgetDescriptor.FromType(typeof(ScriptedWidget)));
    }

    public sealed class Dependency;

    [Widget("test.discovered", "Discovered", Description = "Found by attribute.", DefaultZone = TaskbarZone.Center, MinWidth = 64)]
    public sealed class DiscoveredWidget(WidgetContext context, Dependency dependency) : WidgetBase(context)
    {
        public Dependency Dependency { get; } = dependency;

        public WidgetContext ReceivedContext => Context;

        public override object ViewModel { get; } = new();

        protected override void OnActivated(IWidgetScope scope)
        {
        }
    }
}

public class WidgetHostLifecycleTests
{
    private readonly FakeUiDispatcher _ui = new();

    [Fact]
    public void Widget_is_created_lazily_and_follows_its_lifecycle()
    {
        var created = 0;
        var widget = new ScriptedWidget();
        var host = TestHost.Create(_ui, (_, _) =>
        {
            created++;
            return widget;
        });

        Assert.Equal(WidgetState.NotCreated, host.State);
        Assert.Equal(0, created);

        host.Activate();
        Assert.Equal(1, created);
        Assert.Equal(WidgetState.Active, host.State);
        Assert.Same(widget.ViewModel, host.View);

        host.Deactivate();
        Assert.Equal(WidgetState.Ready, host.State);

        host.Activate();
        Assert.Equal(WidgetState.Active, host.State);

        host.Dispose();
        Assert.Equal(WidgetState.Disposed, host.State);
        Assert.True(widget.IsDisposed);
        Assert.Equal(1, created);
        Assert.Equal(["load", "initialize", "activate", "deactivate", "activate", "deactivate", "dispose"], widget.Log);
    }

    [Fact]
    public void Settings_are_loaded_silently_before_initialization()
    {
        TypedWidget? widget = null;
        var host = TestHost.Create(_ui, (_, context) => widget = new TypedWidget(context), TestHost.Settings(null, ("minutes", "40"), ("sound", "false")));

        host.Activate();

        // Initialization already saw the typed settings, and nobody was told they "changed".
        Assert.Equal(["initialize minutes=40 sound=False", "activate"], widget!.Log);
    }

    [Fact]
    public void Showing_the_view_starts_the_widget_but_does_not_activate_it()
    {
        var widget = new ScriptedWidget();
        var host = TestHost.Create(_ui, (_, _) => widget);

        Assert.Same(widget.ViewModel, host.View);

        Assert.Equal(WidgetState.Ready, host.State);
        Assert.Equal(["load", "initialize"], widget.Log);
    }

    [Fact]
    public void Activation_waits_for_asynchronous_initialization()
    {
        var initialized = new TaskCompletionSource();
        var widget = new ScriptedWidget { Initialize = _ => initialized.Task };
        var host = TestHost.Create(_ui, (_, _) => widget);

        host.Activate();
        _ui.Pump();
        Assert.Equal(WidgetState.Initializing, host.State);
        Assert.Same(widget.ViewModel, host.View); // the slot can already show the (empty) model
        Assert.DoesNotContain("activate", widget.Log);

        initialized.SetResult();
        _ui.PumpUntil(() => host.State == WidgetState.Active);

        Assert.Equal(["load", "initialize", "activate"], widget.Log);
    }

    [Fact]
    public void A_deactivation_that_arrives_during_initialization_is_honored()
    {
        var initialized = new TaskCompletionSource();
        var widget = new ScriptedWidget { Initialize = _ => initialized.Task };
        var host = TestHost.Create(_ui, (_, _) => widget);

        host.Activate();
        host.Deactivate(); // for example a full-screen app started while the widget was still loading
        initialized.SetResult();
        _ui.PumpUntil(() => host.State == WidgetState.Ready);

        Assert.Equal(["load", "initialize"], widget.Log);

        host.Activate();
        Assert.Equal(WidgetState.Active, host.State);
        Assert.Equal(["load", "initialize", "activate"], widget.Log);
    }

    [Fact]
    public void Disposing_during_initialization_cancels_it_and_ignores_its_completion()
    {
        var initialized = new TaskCompletionSource();
        var widget = new ScriptedWidget { Initialize = _ => initialized.Task };
        var host = TestHost.Create(_ui, (_, _) => widget);
        host.Activate();

        host.Dispose();
        Assert.True(widget.InitializationToken.IsCancellationRequested);
        Assert.True(widget.IsDisposed);

        initialized.SetResult();
        _ui.PumpFor(50);

        Assert.Equal(WidgetState.Disposed, host.State);
        Assert.Equal(["load", "initialize", "dispose"], widget.Log);
    }

    [Fact]
    public void Settings_changed_during_initialization_are_applied_once_it_completes_and_before_activation()
    {
        var initialized = new TaskCompletionSource();
        var widget = new ScriptedWidget { Initialize = _ => initialized.Task };
        var host = TestHost.Create(_ui, (_, _) => widget);
        host.Activate();

        host.Apply(TestHost.Settings(null, ("k", "1")));
        host.Apply(TestHost.Settings(null, ("k", "2")));
        Assert.DoesNotContain("apply", widget.Log);

        initialized.SetResult();
        _ui.PumpUntil(() => host.State == WidgetState.Active);

        Assert.Equal(["load", "initialize", "apply", "activate"], widget.Log);
        Assert.Equal("2", (string?)widget.AppliedSettings!["k"]);
    }

    [Fact]
    public void Width_and_settings_updates_are_applied_without_recreation()
    {
        TypedWidget? widget = null;
        var created = 0;
        var host = TestHost.Create(_ui, (_, context) =>
        {
            created++;
            return widget = new TypedWidget(context);
        }, TestHost.Settings(120));
        host.Activate();

        host.Apply(TestHost.Settings(500, ("Minutes", "50")));

        Assert.Equal(200, host.Width);
        Assert.Equal(1, created);
        Assert.Equal("changed minutes 25 -> 50", widget!.Log[^1]);

        // Unchanged options are not re-applied.
        var entries = widget.Log.Count;
        host.Apply(TestHost.Settings(140, ("Minutes", "50")));
        Assert.Equal(140, host.Width);
        Assert.Equal(entries, widget.Log.Count);
    }

    [Fact]
    public void State_changes_are_announced()
    {
        var states = new List<WidgetState>();
        var host = TestHost.Create(_ui, (_, _) => new ScriptedWidget());
        host.StateChanged += (_, _) => states.Add(host.State);

        host.Activate();
        host.Deactivate();
        host.Dispose();

        Assert.Equal([WidgetState.Initializing, WidgetState.Ready, WidgetState.Active, WidgetState.Ready, WidgetState.Disposed], states);
    }

    [Fact]
    public void A_view_can_reach_its_widget_only_through_model_commands()
    {
        TypedWidget? widget = null;
        var host = TestHost.Create(_ui, (_, context) => widget = new TypedWidget(context));
        host.Activate();
        var model = Assert.IsType<TypedModel>(host.View);

        Assert.True(model.Poke.CanExecute());
        model.Poke.Execute();

        Assert.Equal("poke", widget!.Log[^1]);
        Assert.False(new WidgetCommand().CanExecute()); // a command nobody handles is disabled
    }
}

public class WidgetFaultIsolationTests
{
    private readonly FakeUiDispatcher _ui = new();

    [Fact]
    public void A_widget_that_cannot_be_created_is_isolated_behind_a_placeholder()
    {
        var host = TestHost.Create(_ui, (_, _) => throw new InvalidOperationException("boom"));

        host.Activate();

        Assert.Equal(WidgetState.Faulted, host.State);
        Assert.Equal("boom", host.LastError!.Message);
        var placeholder = Assert.IsType<WidgetFaultPlaceholder>(host.View);
        Assert.Equal("Widget error", placeholder.Message);
    }

    [Fact]
    public void A_widget_that_throws_on_activate_is_isolated_and_disposed()
    {
        var widget = new ScriptedWidget { OnActivate = _ => throw new InvalidOperationException("boom") };
        var host = TestHost.Create(_ui, (_, _) => widget);

        host.Activate();

        Assert.Equal(WidgetState.Faulted, host.State);
        Assert.True(widget.IsDisposed);
        Assert.IsType<WidgetFaultPlaceholder>(host.View);
    }

    [Fact]
    public void A_failed_initialization_faults_the_widget()
    {
        var widget = new ScriptedWidget { Initialize = _ => Task.FromException(new InvalidOperationException("no network")) };
        var host = TestHost.Create(_ui, (_, _) => widget);

        host.Activate();

        Assert.Equal(WidgetState.Faulted, host.State);
        Assert.Equal("no network", host.LastError!.Message);
        Assert.True(widget.IsDisposed);
        Assert.DoesNotContain("activate", widget.Log);
    }

    [Fact]
    public void An_initialization_that_hangs_is_cancelled_and_faults_the_widget()
    {
        var widget = new ScriptedWidget { Initialize = _ => new TaskCompletionSource().Task };
        var host = TestHost.Create(_ui, (_, _) => widget, initializeTimeout: TimeSpan.FromMilliseconds(50));

        host.Activate();
        _ui.PumpUntil(() => host.State == WidgetState.Faulted);

        Assert.IsType<TimeoutException>(host.LastError);
        Assert.True(widget.InitializationToken.IsCancellationRequested);
        Assert.True(widget.IsDisposed);
    }

    [Fact]
    public void A_throwing_timer_tick_faults_the_widget_and_stops_everything_it_started()
    {
        var owned = new Disposable();
        var widget = new ScriptedWidget
        {
            OnActivate = scope =>
            {
                scope.Every(TimeSpan.FromSeconds(1), () => throw new InvalidOperationException("tick failed"));
                scope.Own(owned);
            },
        };
        var host = TestHost.Create(_ui, (_, _) => widget);
        var states = new List<WidgetState>();
        host.Activate();
        host.StateChanged += (_, _) => states.Add(host.State);
        var tick = Assert.Single(_ui.LiveTimers);

        tick.Fire(); // must not throw: the bar keeps running

        Assert.Equal(WidgetState.Faulted, host.State);
        Assert.Equal([WidgetState.Faulted], states);
        Assert.Equal("tick failed", host.LastError!.Message);
        Assert.True(tick.IsDisposed);
        Assert.True(owned.IsDisposed);
        Assert.True(widget.Scope!.Token.IsCancellationRequested);
        Assert.True(widget.IsDisposed);
        Assert.IsType<WidgetFaultPlaceholder>(host.View);
    }

    [Fact]
    public void A_throwing_command_handler_faults_the_widget_instead_of_the_caller()
    {
        TypedWidget? widget = null;
        var host = TestHost.Create(_ui, (_, context) => widget = new TypedWidget(context) { ThrowOnPoke = true });
        host.Activate();
        var model = Assert.IsType<TypedModel>(host.View);

        model.Poke.Execute();

        Assert.Equal(WidgetState.Faulted, host.State);

        // A stale view that still holds the old model cannot reach the dead widget any more.
        widget!.Log.Clear();
        model.Poke.Execute();
        Assert.Empty(widget.Log);
    }

    [Fact]
    public void A_faulted_widget_restarts_automatically_with_back_off_and_then_stays_down()
    {
        var created = new List<ScriptedWidget>();
        var failing = true;
        var host = TestHost.Create(_ui, (_, _) =>
        {
            var widget = new ScriptedWidget();
            if (failing)
            {
                widget.OnActivate = _ => throw new InvalidOperationException("still broken");
            }

            created.Add(widget);
            return widget;
        });

        host.Activate();
        Assert.Equal(WidgetState.Faulted, host.State);

        // Three automatic attempts, each after a longer wait.
        foreach (var delay in WidgetHost.RestartDelays)
        {
            var restart = Assert.Single(_ui.LiveTimers);
            Assert.Equal(delay, restart.Interval);
            restart.Fire();
            Assert.Equal(WidgetState.Faulted, host.State);
        }

        Assert.Equal(1 + WidgetHost.RestartDelays.Count, created.Count);
        Assert.All(created, w => Assert.True(w.IsDisposed));
        Assert.Empty(_ui.LiveTimers); // gave up

        // A click on the placeholder restarts it by hand; this time it works and is active right away.
        failing = false;
        Assert.IsType<WidgetFaultPlaceholder>(host.View).Restart();

        Assert.Equal(WidgetState.Active, host.State);
        Assert.Same(created[^1].ViewModel, host.View);
        Assert.False(created[^1].IsDisposed);
    }

    [Fact]
    public void A_restart_while_the_bar_is_hidden_does_not_activate_the_widget()
    {
        var failing = true;
        ScriptedWidget? latest = null;
        var host = TestHost.Create(_ui, (_, _) => latest = new ScriptedWidget
        {
            OnActivate = _ =>
            {
                if (failing)
                {
                    throw new InvalidOperationException("boom");
                }
            },
        });
        host.Activate();
        host.Deactivate();
        failing = false;

        Assert.Single(_ui.LiveTimers).Fire();

        Assert.Equal(WidgetState.Ready, host.State);
        Assert.DoesNotContain("activate", latest!.Log);
    }

    [Fact]
    public void Disposing_a_faulted_widget_cancels_its_pending_restart()
    {
        var host = TestHost.Create(_ui, (_, _) => throw new InvalidOperationException("boom"));
        host.Activate();
        Assert.Single(_ui.LiveTimers);

        host.Dispose();

        Assert.Empty(_ui.LiveTimers);
        Assert.Equal(WidgetState.Disposed, host.State);
    }

    [Fact]
    public void A_widget_that_throws_while_being_disposed_does_not_break_the_host()
    {
        var widget = new ScriptedWidget { OnDeactivate = () => throw new InvalidOperationException("boom") };
        var host = TestHost.Create(_ui, (_, _) => widget);
        host.Activate();

        host.Dispose();

        Assert.Equal(WidgetState.Disposed, host.State);
        Assert.True(widget.IsDisposed);
    }

    private sealed class Disposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}

internal enum TypedMode
{
    Compact,
    Detailed,
}

internal sealed class TypedSettings
{
    public int Minutes { get; set; } = 25;
    public bool Sound { get; set; } = true;
    public TypedMode Mode { get; set; } = TypedMode.Compact;
}

internal sealed class TypedModel
{
    public WidgetCommand Poke { get; } = new();
}

/// <summary>A widget written the way real ones are: typed settings, typed model, a command wired to a handler.</summary>
internal sealed class TypedWidget : WidgetBase<TypedSettings, TypedModel>
{
    public TypedWidget(WidgetContext context)
        : base(context, new TypedModel())
    {
        On(Model.Poke, () =>
        {
            Log.Add("poke");
            if (ThrowOnPoke)
            {
                throw new InvalidOperationException("poke failed");
            }
        });
    }

    public List<string> Log { get; } = new();

    public bool ThrowOnPoke { get; init; }

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Log.Add($"initialize minutes={Settings.Minutes} sound={Settings.Sound}");
        return Task.CompletedTask;
    }

    protected override void OnActivated(IWidgetScope scope) => Log.Add("activate");

    protected override void OnSettingsChanged(TypedSettings previous) =>
        Log.Add($"changed minutes {previous.Minutes} -> {Settings.Minutes}");
}

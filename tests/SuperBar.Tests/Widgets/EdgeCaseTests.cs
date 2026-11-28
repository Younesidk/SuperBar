using System.ComponentModel;
using SuperBar.Core.Mvvm;
using SuperBar.Widgets;

namespace SuperBar.Tests.Widgets;

/// <summary>
/// The Loaded / Unloaded rules behind every widget view (<c>WidgetViewBase</c> forwards its WinUI events to
/// <see cref="LoadedSubscription"/>), including the out-of-order events WinUI sends when a view is re-parented.
/// </summary>
public class LoadedSubscriptionTests
{
    private readonly Source _source = new();
    private int _refreshes;

    private LoadedSubscription Create() => new([_source], () => _refreshes++);

    [Fact]
    public void A_view_listens_only_while_loaded_and_catches_up_when_it_comes_back()
    {
        var subscription = Create();
        _source.Raise();
        Assert.Equal(0, _refreshes);

        subscription.OnLoaded();
        Assert.Equal(1, _refreshes); // catch-up on load
        _source.Raise();
        Assert.Equal(2, _refreshes);

        subscription.OnUnloaded(stillLoaded: false);
        _source.Raise();
        Assert.Equal(2, _refreshes);
        Assert.Equal(0, _source.Listeners);

        subscription.OnLoaded(); // moved to another zone: alive again, not dead
        Assert.Equal(3, _refreshes);
        _source.Raise();
        Assert.Equal(4, _refreshes);
        Assert.Equal(1, _source.Listeners);
    }

    [Fact]
    public void A_stale_unloaded_after_the_new_loaded_keeps_the_view_alive()
    {
        var subscription = Create();
        subscription.OnLoaded();

        // Re-parenting: the new parent's Loaded arrives first, then the old parent's Unloaded.
        subscription.OnLoaded();
        subscription.OnUnloaded(stillLoaded: true);

        Assert.True(subscription.IsAttached);
        Assert.Equal(1, _source.Listeners);
        _refreshes = 0;
        _source.Raise();
        Assert.Equal(1, _refreshes); // exactly once: not double-subscribed
    }

    [Fact]
    public void Repeated_loaded_events_never_double_subscribe()
    {
        var subscription = Create();

        for (var i = 0; i < 5; i++)
        {
            subscription.OnLoaded();
        }

        Assert.Equal(1, _source.Listeners);
    }

    [Fact]
    public void A_disposed_view_stays_detached_even_if_it_is_loaded_again()
    {
        var subscription = Create();
        subscription.OnLoaded();

        subscription.Dispose();
        subscription.OnLoaded();

        Assert.False(subscription.IsAttached);
        Assert.Equal(0, _source.Listeners);
    }

    private sealed class Source : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _handler;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add
            {
                _handler += value;
                Listeners++;
            }

            remove
            {
                _handler -= value;
                Listeners--;
            }
        }

        public int Listeners { get; private set; }

        public void Raise() => _handler?.Invoke(this, new PropertyChangedEventArgs("X"));
    }
}

public class WidgetCommandWiringTests
{
    private readonly FakeUiDispatcher _ui = new();

    [Fact]
    public void Activation_cycles_never_stack_command_handlers()
    {
        CommandWidget? widget = null;
        var host = TestHost.Create(_ui, (_, context) => widget = new CommandWidget(context));
        var model = Assert.IsType<CommandModel>(host.View);

        for (var i = 0; i < 5; i++)
        {
            host.Activate();
            host.Deactivate();
        }

        host.Activate();
        model.Click.Execute();
        model.Rewired.Execute();

        Assert.Equal(1, widget!.Clicks);   // wired once in the constructor
        Assert.Equal(1, widget.Rewires);   // re-wired on every activation, still runs once
    }

    [Fact]
    public void Disposing_the_widget_unbinds_its_commands()
    {
        var host = TestHost.Create(_ui, (_, context) => new CommandWidget(context));
        host.Activate();
        var model = Assert.IsType<CommandModel>(host.View);
        Assert.True(model.Click.CanExecute());

        var canExecuteChanged = 0;
        model.Click.CanExecuteChanged += (_, _) => canExecuteChanged++;
        host.Dispose();

        Assert.False(model.Click.CanExecute()); // a view that outlives the widget shows the command as disabled
        Assert.False(model.Rewired.CanExecute());
        Assert.Equal(1, canExecuteChanged);
    }

    [Fact]
    public void A_restarted_widget_owns_the_commands_of_its_own_model_only()
    {
        var created = new List<CommandWidget>();
        var failFirst = true;
        var host = TestHost.Create(_ui, (_, context) =>
        {
            var widget = new CommandWidget(context) { ThrowOnActivate = failFirst };
            failFirst = false;
            created.Add(widget);
            return widget;
        });
        host.Activate();
        var deadModel = created[0].Model;

        Assert.IsType<WidgetFaultPlaceholder>(host.View).Restart();
        var liveModel = Assert.IsType<CommandModel>(host.View);
        deadModel.Click.Execute();
        liveModel.Click.Execute();

        Assert.False(deadModel.Click.CanExecute());
        Assert.Equal(0, created[0].Clicks);
        Assert.Equal(1, created[1].Clicks);
    }

    private sealed class CommandModel
    {
        public WidgetCommand Click { get; } = new();
        public WidgetCommand Rewired { get; } = new();
    }

    private sealed class CommandWidget : WidgetBase<NoSettings, CommandModel>
    {
        public CommandWidget(WidgetContext context)
            : base(context, new CommandModel()) =>
            On(Model.Click, () => Clicks++);

        public int Clicks { get; private set; }
        public int Rewires { get; private set; }
        public bool ThrowOnActivate { get; init; }

        protected override void OnActivated(IWidgetScope scope)
        {
            if (ThrowOnActivate)
            {
                throw new InvalidOperationException("boom");
            }

            On(Model.Rewired, () => Rewires++);
        }
    }

    private sealed class NoSettings;
}

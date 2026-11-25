using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;

namespace SuperBar.Widgets;

public enum WidgetState
{
    /// <summary>Nothing exists yet; the widget is created on first use.</summary>
    NotCreated,

    /// <summary>Created, settings loaded, <see cref="IWidget.InitializeAsync"/> is running.</summary>
    Initializing,

    /// <summary>Initialized but not visible: no scope, no timers, no background work.</summary>
    Ready,

    /// <summary>Visible, with a live <see cref="IWidgetScope"/>.</summary>
    Active,

    /// <summary>The widget threw; it was disposed and its slot shows a <see cref="WidgetFaultPlaceholder"/>.</summary>
    Faulted,

    Disposed,
}

/// <summary>
/// Owns one widget instance inside the taskbar and is the only caller of its <see cref="IWidget"/> members.
/// UI thread only.
/// <para>State machine:</para>
/// <code>
/// NotCreated ──create, LoadSettings──▶ Initializing ──InitializeAsync done──▶ Ready ⇄ Active
///      ▲                                    │                                   │       │
///      └────────── Restart ──────────── Faulted ◀──────── any exception ────────┴───────┘
/// </code>
/// <list type="bullet">
/// <item>Settings are loaded silently before initialization, so <see cref="IWidget.InitializeAsync"/> sees typed settings.</item>
/// <item><see cref="Activate"/> and <see cref="Deactivate"/> only record what the bar wants while the widget is
/// initializing; the widget is activated when initialization completes if the bar still wants it active.</item>
/// <item>Settings changed during initialization are applied once, right after it.</item>
/// <item>Every call into the widget is guarded: an exception disposes the widget, shows the placeholder and
/// schedules a restart (see <see cref="RestartDelays"/>).</item>
/// </list>
/// </summary>
public sealed class WidgetHost : IDisposable
{
    /// <summary>Waits between a fault and the automatic restarts. After the last one the widget stays faulted until restarted by hand.</summary>
    public static readonly IReadOnlyList<TimeSpan> RestartDelays =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>A UI-thread callback of a widget that takes longer than this is logged as a warning.</summary>
    public static readonly TimeSpan SlowCallbackThreshold = TimeSpan.FromMilliseconds(50);

    // A widget that ran this long without faulting gets its automatic restarts back.
    private static readonly TimeSpan HealthyPeriod = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private WidgetInstanceSettings _settings;
    private IWidget? _widget;
    private object? _viewModel;
    private WidgetFaultPlaceholder? _faultView;
    private WidgetScope? _scope;
    private CancellationTokenSource? _initCts;
    private IUiTimer? _restartTimer;
    private int _generation;
    private int _automaticRestarts;
    private long _lastFaultAt;
    private readonly IWidgetStateStore _state;
    private bool _wantActive;

    // The options the live widget currently has (loaded, applied or saved by itself). Incoming settings are compared
    // with this, so a widget's own save does not come back to it as a change.
    private JsonObject? _delivered;

    public WidgetHost(WidgetDescriptor descriptor, WidgetInstanceSettings settings, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        Descriptor = descriptor;
        _settings = settings;
        _services = services;
        _dispatcher = services.GetRequiredService<IUiDispatcher>();
        _logger = loggerFactory.CreateLogger($"SuperBar.Widget.{descriptor.TypeId}");

        // Survives restarts of a faulted widget: state belongs to the instance, not to one widget object.
        _state = services.GetService<ISettingsStore>() is { } store
            ? new SettingsStoreWidgetState(store, settings.InstanceId, _logger)
            : new InMemoryWidgetStateStore();
    }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes (and with it, possibly, <see cref="View"/>).</summary>
    public event EventHandler? StateChanged;

    public WidgetDescriptor Descriptor { get; }

    public string InstanceId => _settings.InstanceId;

    public WidgetState State { get; private set; } = WidgetState.NotCreated;

    /// <summary>The exception behind the current or most recent <see cref="WidgetState.Faulted"/> state.</summary>
    public Exception? LastError { get; private set; }

    public double Width => Descriptor.ResolveWidth(_settings.Width);

    /// <summary>How long <see cref="IWidget.InitializeAsync"/> may take before the widget is faulted.</summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The content of the taskbar slot: the widget's presentation model, or a <see cref="WidgetFaultPlaceholder"/>
    /// while it is faulted. Accessing it creates the widget on first use.
    /// </summary>
    public object View
    {
        get
        {
            EnsureStarted();
            return _viewModel ?? (_faultView ??= new WidgetFaultPlaceholder(Descriptor.DisplayName, Restart));
        }
    }

    /// <summary>The bar shows the widget. It becomes <see cref="WidgetState.Active"/> now, or as soon as it has initialized.</summary>
    public void Activate()
    {
        _wantActive = true;
        switch (State)
        {
            case WidgetState.NotCreated:
                EnsureStarted();
                break;
            case WidgetState.Ready:
                EnterActive();
                break;
        }
    }

    /// <summary>The bar hides or suspends the widget. Safe in every state, including while it initializes.</summary>
    public void Deactivate()
    {
        _wantActive = false;
        if (State == WidgetState.Active)
        {
            ExitActive();
        }
    }

    /// <summary>Applies changed instance settings (width, options) without recreating the widget.</summary>
    public void Apply(WidgetInstanceSettings settings)
    {
        _settings = settings;

        // While initializing, the change is delivered right after initialization (see OnInitialized).
        if (State is WidgetState.Ready or WidgetState.Active && OptionsChanged())
        {
            ApplySettingsNow();
        }
    }

    /// <summary>Recreates a faulted widget now. Also what a click on the error placeholder does.</summary>
    public void Restart()
    {
        _automaticRestarts = 0;
        RestartCore();
    }

    public void Dispose()
    {
        if (State == WidgetState.Disposed)
        {
            return;
        }

        var wasActive = State == WidgetState.Active;
        var widget = _widget;
        StopRestartTimer();
        DisposeScope();
        CancelInitialization();

        // From here on a throwing widget is only logged.
        State = WidgetState.Disposed;
        _generation++;
        if (widget is not null)
        {
            if (wasActive)
            {
                Quietly(widget.Deactivate, "deactivate");
            }

            Quietly(widget.Dispose, "dispose");
        }

        _widget = null;
        _viewModel = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        StateChanged = null;
    }

    private void EnsureStarted()
    {
        if (State != WidgetState.NotCreated)
        {
            return;
        }

        var generation = ++_generation;
        var context = new WidgetContext(
            InstanceId,
            _logger,
            _state,
            (action, operation) =>
            {
                // Callbacks of a widget that has since been replaced or disposed are dropped.
                if (generation == _generation && _widget is not null)
                {
                    Guard(action, operation);
                }
            },
            values =>
            {
                if (generation == _generation && _widget is not null)
                {
                    SaveOptions(values);
                }
            });

        IWidget widget;
        try
        {
            widget = Descriptor.Factory(_services, context) ?? throw new InvalidOperationException("The widget factory returned null.");
        }
        catch (Exception ex)
        {
            Fault(ex, "create");
            return;
        }

        _widget = widget;
        try
        {
            _viewModel = widget.ViewModel ?? throw new InvalidOperationException("The widget has no view model.");

            // Silent: the widget starts with its settings, it is not told they "changed".
            _delivered = Snapshot(_settings.Options);
            widget.LoadSettings(Snapshot(_settings.Options));
        }
        catch (Exception ex)
        {
            Fault(ex, "load settings");
            return;
        }

        _logger.LogDebug("Widget {InstanceId} created", InstanceId);
        var cts = _initCts = new CancellationTokenSource();
        SetState(WidgetState.Initializing);
        if (State != WidgetState.Initializing)
        {
            return; // a StateChanged handler disposed the host
        }

        Task initialization;
        try
        {
            initialization = widget.InitializeAsync(cts.Token) ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Fault(ex, "initialize");
            return;
        }

        if (initialization.IsCompleted)
        {
            // The common case (nothing to await): no flicker, no extra dispatcher round trip.
            OnInitialized(generation, initialization.IsFaulted
                ? initialization.Exception!.GetBaseException()
                : initialization.IsCanceled ? new OperationCanceledException("Initialization was cancelled.") : null);
        }
        else
        {
            _ = AwaitInitializationAsync(initialization, generation, cts.Token);
        }
    }

    private async Task AwaitInitializationAsync(Task initialization, int generation, CancellationToken token)
    {
        Exception? error = null;
        try
        {
            await initialization.WaitAsync(InitializeTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            error = new TimeoutException($"The widget did not finish initializing within {InitializeTimeout.TotalSeconds:0.##} s.");
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // An abandoned initialization (timeout, dispose) must not surface as an unobserved task exception.
        _ = initialization.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        _dispatcher.TryEnqueue(() => OnInitialized(generation, error));
    }

    private void OnInitialized(int generation, Exception? error)
    {
        // Disposed, faulted or restarted while initializing: this completion belongs to a widget that is gone.
        if (generation != _generation || State != WidgetState.Initializing)
        {
            return;
        }

        if (error is not null)
        {
            // Also cancels the initialization token: one that timed out is told to stop.
            Fault(error, "initialize");
            return;
        }

        _initCts = null;
        SetState(WidgetState.Ready);
        if (State == WidgetState.Ready && OptionsChanged())
        {
            ApplySettingsNow();
        }

        // Activate only if the bar still wants it: Deactivate may have arrived while initializing.
        if (State == WidgetState.Ready && _wantActive)
        {
            EnterActive();
        }
    }

    private void EnterActive()
    {
        var widget = _widget!;
        var scope = _scope = new WidgetScope(_dispatcher, _logger, Guard);
        SetState(WidgetState.Active);
        if (State == WidgetState.Active && ReferenceEquals(_scope, scope))
        {
            Guard(() => widget.Activate(scope), "activate");
        }
    }

    private void ExitActive()
    {
        var widget = _widget!;

        // The scope ends first: when the widget hears about it, nothing it started is running any more.
        DisposeScope();
        SetState(WidgetState.Ready);
        if (State == WidgetState.Ready)
        {
            Guard(widget.Deactivate, "deactivate");
        }
    }

    private void ApplySettingsNow()
    {
        var widget = _widget!;
        _delivered = Snapshot(_settings.Options);
        var settings = Snapshot(_settings.Options);
        Guard(() => widget.ApplySettings(settings), "apply settings");
    }

    private bool OptionsChanged() => !JsonNode.DeepEquals(_delivered, _settings.Options);

    /// <summary>
    /// A settings save made by the widget itself (<c>SaveSettings</c>): merged into this instance's options and
    /// persisted. The widget already has these values, so the resulting settings change is not delivered back.
    /// </summary>
    private void SaveOptions(JsonObject values)
    {
        var merged = WidgetOptions.Merge(_settings.Options, values);
        _delivered = Snapshot(merged);
        _settings = new WidgetInstanceSettings
        {
            InstanceId = _settings.InstanceId,
            TypeId = _settings.TypeId,
            Enabled = _settings.Enabled,
            Width = _settings.Width,
            Options = merged,
        };

        if (_services.GetService<ISettingsService>() is not { } settingsService)
        {
            return;
        }

        try
        {
            settingsService.Update(s =>
            {
                if (s.Widgets.FirstOrDefault(w => w.InstanceId == InstanceId) is { } instance)
                {
                    instance.Options = Snapshot(merged);
                }
            });
        }
        catch (Exception ex)
        {
            // Not the widget's fault: it keeps running with the new values, they are just not on disk.
            _logger.LogError(ex, "Could not save the settings of widget {InstanceId}", InstanceId);
        }
    }

    /// <summary>Runs widget code on the UI thread. An exception faults the widget instead of propagating.</summary>
    private void Guard(Action action, string operation)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Fault(ex, operation);
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed > SlowCallbackThreshold)
        {
            _logger.LogWarning(
                "Widget {InstanceId} blocked the UI thread for {Elapsed:0} ms ({Operation}); move the work into IWidgetScope.Poll or Run",
                InstanceId, elapsed.TotalMilliseconds, operation);
        }
    }

    private void Fault(Exception ex, string operation)
    {
        if (State is WidgetState.Disposed or WidgetState.Faulted)
        {
            _logger.LogWarning(ex, "Widget {InstanceId} failed to {Operation} after it was already torn down", InstanceId, operation);
            return;
        }

        _logger.LogError(ex, "Widget {InstanceId} failed to {Operation}; showing placeholder", InstanceId, operation);
        LastError = ex;

        var widget = _widget;
        _widget = null;
        _viewModel = null;
        DisposeScope();
        CancelInitialization();
        if (widget is not null)
        {
            Quietly(widget.Dispose, "dispose");
        }

        _faultView = new WidgetFaultPlaceholder(Descriptor.DisplayName, Restart);
        ScheduleRestart();
        SetState(WidgetState.Faulted);
    }

    private void ScheduleRestart()
    {
        var now = Environment.TickCount64;
        if (now - _lastFaultAt > HealthyPeriod.TotalMilliseconds)
        {
            _automaticRestarts = 0;
        }

        _lastFaultAt = now;
        StopRestartTimer();
        if (_automaticRestarts >= RestartDelays.Count)
        {
            _logger.LogError("Widget {InstanceId} keeps failing; it stays disabled until it is restarted by hand", InstanceId);
            return;
        }

        var delay = RestartDelays[_automaticRestarts++];
        _logger.LogInformation("Widget {InstanceId} will restart in {Delay}", InstanceId, delay);
        _restartTimer = _dispatcher.CreateTimer(delay, RestartCore);
        _restartTimer.Start();
    }

    private void RestartCore()
    {
        StopRestartTimer();
        if (State != WidgetState.Faulted)
        {
            return;
        }

        _logger.LogInformation("Restarting widget {InstanceId}", InstanceId);
        _faultView = null;
        State = WidgetState.NotCreated;

        // Creates and initializes again; it is activated afterwards if the bar wants it active.
        EnsureStarted();
    }

    private void StopRestartTimer()
    {
        _restartTimer?.Stop();
        _restartTimer?.Dispose();
        _restartTimer = null;
    }

    private void DisposeScope()
    {
        var scope = _scope;
        _scope = null;
        scope?.Dispose();
    }

    private void CancelInitialization()
    {
        var cts = _initCts;
        _initCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex, "A cancellation callback of widget {InstanceId} failed", InstanceId);
        }
    }

    private void Quietly(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Widget {InstanceId} failed to {Operation} while being torn down", InstanceId, operation);
        }
    }

    private void SetState(WidgetState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Settings snapshots are shared and immutable, and a JSON node can only have one parent: widgets get copies.
    private static JsonObject Snapshot(JsonObject? options) => options is null ? new JsonObject() : (JsonObject)options.DeepClone();
}

/// <summary>
/// Shown in place of a widget that threw. A plain model (no UI types), so fault isolation works and is testable
/// without a UI thread; the taskbar renders it as a subtle "Widget error" label that restarts the widget on click.
/// </summary>
public sealed class WidgetFaultPlaceholder(string widgetName, Action restart)
{
    public string WidgetName { get; } = widgetName;

    public string Message => "Widget error";

    public string ToolTip => $"{WidgetName} stopped working. Click to restart it.";

    public void Restart() => restart();
}

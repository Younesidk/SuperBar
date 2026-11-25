using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SuperBar.Widgets;

/// <summary>
/// A widget that lives inside the taskbar surface (never in its own window). Derive from
/// <see cref="WidgetBase"/> or <see cref="WidgetBase{TSettings, TModel}"/> instead of implementing this directly.
/// <para>
/// The host calls the members in this order, always on the UI thread:
/// constructor → <see cref="LoadSettings"/> (once) → <see cref="InitializeAsync"/> (once) →
/// <see cref="Activate"/> ⇄ <see cref="Deactivate"/> (any number of times) → <see cref="IDisposable.Dispose"/>.
/// <see cref="ApplySettings"/> only arrives after initialization has completed.
/// </para>
/// </summary>
public interface IWidget : IDisposable
{
    /// <summary>
    /// The UI-framework-free presentation model the taskbar renders with the view registered for its type
    /// (<c>[WidgetViewFor(typeof(TModel))]</c> in SuperBar.Runtime). Created once; must not change.
    /// </summary>
    object ViewModel { get; }

    /// <summary>Receives the persisted settings before <see cref="InitializeAsync"/>. Must not raise change notifications.</summary>
    void LoadSettings(JsonObject settings);

    /// <summary>One-time start-up work. May be asynchronous; must honor <paramref name="cancellationToken"/>.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The widget is visible. Start all recurring and background work through <paramref name="scope"/>:
    /// the host tears the scope down before <see cref="Deactivate"/>. Must return immediately.
    /// </summary>
    void Activate(IWidgetScope scope);

    /// <summary>The widget is hidden or suspended. Its scope has already been torn down.</summary>
    void Deactivate();

    /// <summary>The user changed this instance's settings while the widget is alive.</summary>
    void ApplySettings(JsonObject settings);
}

/// <summary>
/// Everything a widget runs while it is active. The scope lives from <see cref="IWidget.Activate"/> to
/// <see cref="IWidget.Deactivate"/>: when it ends, timers stop, <see cref="Token"/> is cancelled, owned
/// subscriptions are disposed and pending results are dropped. Call its members on the UI thread.
/// <para>
/// Every callback runs on the UI thread behind the host's fault guard: a callback that throws faults the
/// widget (error placeholder, automatic restart) instead of the bar.
/// </para>
/// </summary>
public interface IWidgetScope
{
    /// <summary>Cancelled when the widget is deactivated, faulted or disposed.</summary>
    CancellationToken Token { get; }

    /// <summary>Runs <paramref name="tick"/> on the UI thread every <paramref name="interval"/>, and once right away when <paramref name="immediate"/> is set.</summary>
    void Every(TimeSpan interval, Action tick, bool immediate = false);

    /// <summary>Runs <paramref name="action"/> once on the UI thread after <paramref name="delay"/>. Re-arm it from the action for irregular schedules.</summary>
    void After(TimeSpan delay, Action action);

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool every <paramref name="interval"/> (never overlapping) and
    /// hands each result to <paramref name="apply"/> on the UI thread. A run that throws or exceeds
    /// <paramref name="timeout"/> (default 10 s) is reported to <paramref name="onError"/> on the UI thread and
    /// retried with exponential back-off; the widget keeps its last good data and is not faulted.
    /// </summary>
    /// <param name="runImmediately">Run the first time right away instead of after one interval.</param>
    void Poll<T>(
        TimeSpan interval,
        Func<CancellationToken, Task<T>> work,
        Action<T> apply,
        TimeSpan? timeout = null,
        bool runImmediately = true,
        Action<Exception>? onError = null);

    /// <summary>Runs <paramref name="work"/> once on the thread pool and hands the result to <paramref name="apply"/> on the UI thread.</summary>
    void Run<T>(
        Func<CancellationToken, Task<T>> work,
        Action<T> apply,
        TimeSpan? timeout = null,
        Action<Exception>? onError = null);

    /// <summary>Disposes <paramref name="subscription"/> (an OS hook, an event subscription, …) when the scope ends.</summary>
    void Own(IDisposable subscription);
}

/// <summary>What a widget instance receives when it is created.</summary>
public sealed class WidgetContext
{
    private readonly Action<Action, string>? _guard;
    private readonly Action<JsonObject>? _saveSettings;

    /// <summary>
    /// Creates a context without a host (tests): callbacks run unguarded, settings are not persisted, and flyouts
    /// open on <paramref name="flyouts"/> (a private coordinator without a surface when omitted).
    /// </summary>
    public WidgetContext(string instanceId, ILogger logger, IWidgetStateStore? state = null, WidgetFlyoutCoordinator? flyouts = null)
        : this(instanceId, logger, state ?? new InMemoryWidgetStateStore(), new WidgetFlyout(flyouts ?? new WidgetFlyoutCoordinator(null), instanceId, () => true), null, null)
    {
    }

    internal WidgetContext(
        string instanceId,
        ILogger logger,
        IWidgetStateStore state,
        IWidgetFlyoutHost flyout,
        Action<Action, string>? guard,
        Action<JsonObject>? saveSettings)
    {
        InstanceId = instanceId;
        Logger = logger;
        State = state;
        Flyout = flyout;
        _guard = guard;
        _saveSettings = saveSettings;
    }

    public string InstanceId { get; }

    public ILogger Logger { get; }

    /// <summary>This instance's runtime state (not settings). Saving it does not touch the bar's layout.</summary>
    public IWidgetStateStore State { get; }

    /// <summary>This widget's flyout (see <see cref="IWidgetFlyoutHost"/>).</summary>
    public IWidgetFlyoutHost Flyout { get; }

    /// <summary>Persists changed settings values of this instance (merged into its stored options).</summary>
    internal void SaveSettings(JsonObject values) => _saveSettings?.Invoke(values);

    /// <summary>Runs widget code behind the host's fault guard.</summary>
    internal void Invoke(Action action, string operation)
    {
        if (_guard is null)
        {
            action();
        }
        else
        {
            _guard(action, operation);
        }
    }
}

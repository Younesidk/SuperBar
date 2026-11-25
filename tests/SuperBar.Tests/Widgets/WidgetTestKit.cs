using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Widgets;

namespace SuperBar.Tests.Widgets;

/// <summary>
/// A UI thread for tests: the test's own thread. Queued work runs when the test calls <see cref="Pump"/>, and
/// timers tick when the test calls <see cref="FakeTimer.Fire"/>, so nothing depends on real time.
/// </summary>
internal sealed class FakeUiDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public List<FakeTimer> Timers { get; } = new();

    /// <summary>Timers that were created and not yet disposed.</summary>
    public List<FakeTimer> LiveTimers => Timers.Where(t => !t.IsDisposed).ToList();

    public bool HasThreadAccess => true;

    public bool TryEnqueue(Action action)
    {
        _queue.Enqueue(action);
        return true;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new FakeTimer(interval, tick);
        Timers.Add(timer);
        return timer;
    }

    public void Pump()
    {
        while (_queue.TryDequeue(out var action))
        {
            action();
        }
    }

    /// <summary>Pumps until <paramref name="condition"/> holds; for results that arrive from the thread pool.</summary>
    public void PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            if (condition())
            {
                return;
            }

            Assert.True(watch.ElapsedMilliseconds < timeoutMs, "The expected condition was not reached in time.");
            Thread.Sleep(5);
        }
    }

    /// <summary>Pumps for a while to prove that something does NOT happen.</summary>
    public void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Pump();
            Thread.Sleep(5);
        }

        Pump();
    }
}

internal sealed class FakeTimer(TimeSpan interval, Action tick) : IUiTimer
{
    public TimeSpan Interval { get; set; } = interval;
    public bool IsRunning { get; private set; }
    public bool IsDisposed { get; private set; }

    public void Start() => IsRunning = !IsDisposed;

    public void Stop() => IsRunning = false;

    public void Fire()
    {
        Assert.True(IsRunning, "A stopped timer cannot tick.");
        tick();
    }

    public void Dispose()
    {
        IsRunning = false;
        IsDisposed = true;
    }
}

/// <summary>A raw <see cref="IWidget"/> whose behavior is scripted by the test and whose calls are recorded.</summary>
internal sealed class ScriptedWidget : IWidget
{
    public List<string> Log { get; } = new();
    public object ViewModel { get; } = new();
    public Func<CancellationToken, Task> Initialize { get; set; } = _ => Task.CompletedTask;
    public Action<IWidgetScope> OnActivate { get; set; } = _ => { };
    public Action OnDeactivate { get; set; } = () => { };
    public IWidgetScope? Scope { get; private set; }
    public CancellationToken InitializationToken { get; private set; }
    public JsonObject? LoadedSettings { get; private set; }
    public JsonObject? AppliedSettings { get; private set; }
    public bool IsDisposed { get; private set; }

    public void LoadSettings(JsonObject settings)
    {
        Log.Add("load");
        LoadedSettings = settings;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        Log.Add("initialize");
        InitializationToken = cancellationToken;
        return Initialize(cancellationToken);
    }

    public void Activate(IWidgetScope scope)
    {
        Log.Add("activate");
        Scope = scope;
        OnActivate(scope);
    }

    public void Deactivate()
    {
        Log.Add("deactivate");
        OnDeactivate();
    }

    public void ApplySettings(JsonObject settings)
    {
        Log.Add("apply");
        AppliedSettings = settings;
    }

    public void Dispose()
    {
        Log.Add("dispose");
        IsDisposed = true;
    }
}

internal static class TestHost
{
    public const string TypeId = "test.widget";

    public static WidgetInstanceSettings Settings(double? width = null, params (string Key, string Value)[] options)
    {
        var settings = new WidgetInstanceSettings { InstanceId = "w1", TypeId = TypeId, Width = width };
        foreach (var (key, value) in options)
        {
            settings.Options[key] = value;
        }

        return settings;
    }

    public static WidgetHost Create(
        FakeUiDispatcher dispatcher,
        Func<IServiceProvider, WidgetContext, IWidget> factory,
        WidgetInstanceSettings? settings = null,
        TimeSpan? initializeTimeout = null) =>
        Create(dispatcher, new WidgetDescriptor(TypeId, "Test", factory, defaultWidth: 100, minWidth: 50, maxWidth: 200), settings, initializeTimeout);

    public static WidgetHost Create(
        FakeUiDispatcher dispatcher,
        WidgetDescriptor descriptor,
        WidgetInstanceSettings? settings = null,
        TimeSpan? initializeTimeout = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddSingleton<IUiDispatcher>(dispatcher);
        configure?.Invoke(services);
        return new WidgetHost(descriptor, settings ?? Settings(), services.BuildServiceProvider(), NullLoggerFactory.Instance)
        {
            InitializeTimeout = initializeTimeout ?? TimeSpan.FromSeconds(30),
        };
    }
}

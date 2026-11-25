using SuperBar.Widgets;

namespace SuperBar.Tests.Widgets;

/// <summary>What a widget starts through its <see cref="IWidgetScope"/>, exercised through a real <see cref="WidgetHost"/>.</summary>
public class WidgetScopeTests
{
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);

    private readonly FakeUiDispatcher _ui = new();

    private WidgetHost Host(Action<IWidgetScope> onActivate, out ScriptedWidget widget)
    {
        var scripted = widget = new ScriptedWidget { OnActivate = onActivate };
        return TestHost.Create(_ui, (_, _) => scripted);
    }

    [Fact]
    public void A_repeating_timer_runs_only_while_the_widget_is_active()
    {
        var ticks = 0;
        var host = Host(scope => scope.Every(TimeSpan.FromSeconds(2), () => ticks++), out _);

        host.Activate();
        var timer = Assert.Single(_ui.LiveTimers);
        Assert.Equal(TimeSpan.FromSeconds(2), timer.Interval);
        Assert.True(timer.IsRunning);
        Assert.Equal(0, ticks); // not immediate by default

        timer.Fire();
        timer.Fire();
        Assert.Equal(2, ticks);

        host.Deactivate();
        Assert.True(timer.IsDisposed);
        Assert.Empty(_ui.LiveTimers);

        host.Activate();
        Assert.NotSame(timer, Assert.Single(_ui.LiveTimers));
    }

    [Fact]
    public void An_immediate_timer_ticks_once_right_away()
    {
        var ticks = 0;
        var host = Host(scope => scope.Every(TimeSpan.FromSeconds(2), () => ticks++, immediate: true), out _);

        host.Activate();

        Assert.Equal(1, ticks);
    }

    [Fact]
    public void A_delayed_action_runs_once_and_can_re_arm_itself()
    {
        var runs = 0;
        void Arm(IWidgetScope scope) => scope.After(TimeSpan.FromSeconds(runs + 1), () =>
        {
            runs++;
            Arm(scope);
        });
        var host = Host(Arm, out _);

        host.Activate();
        var first = Assert.Single(_ui.LiveTimers);
        Assert.Equal(TimeSpan.FromSeconds(1), first.Interval);

        first.Fire();
        Assert.Equal(1, runs);
        Assert.True(first.IsDisposed);
        var second = Assert.Single(_ui.LiveTimers);
        Assert.Equal(TimeSpan.FromSeconds(2), second.Interval);

        host.Deactivate();
        Assert.Empty(_ui.LiveTimers);
    }

    [Fact]
    public void Deactivation_cancels_the_token_and_releases_owned_subscriptions()
    {
        var subscription = new Subscription();
        var host = Host(scope => scope.Own(subscription), out var widget);
        host.Activate();
        var token = widget.Scope!.Token;
        Assert.False(token.IsCancellationRequested);

        host.Deactivate();

        Assert.True(token.IsCancellationRequested);
        Assert.True(subscription.IsDisposed);

        // Each activation gets a fresh scope.
        host.Activate();
        Assert.False(widget.Scope!.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_scope_that_has_ended_ignores_late_calls()
    {
        var host = Host(_ => { }, out var widget);
        host.Activate();
        var stale = widget.Scope!;
        host.Deactivate();

        var late = new Subscription();
        stale.Every(TimeSpan.FromSeconds(1), () => { });
        stale.After(TimeSpan.FromSeconds(1), () => { });
        stale.Poll(Never, _ => Task.FromResult(1), _ => { });
        stale.Own(late);

        Assert.Empty(_ui.LiveTimers);
        Assert.True(late.IsDisposed);
    }

    [Fact]
    public void Polling_runs_off_the_ui_thread_and_delivers_the_first_result_immediately()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var workThread = 0;
        var applyThread = 0;
        var results = new List<int>();
        var host = Host(
            scope => scope.Poll(
                Never,
                _ =>
                {
                    workThread = Environment.CurrentManagedThreadId;
                    return Task.FromResult(42);
                },
                value =>
                {
                    applyThread = Environment.CurrentManagedThreadId;
                    results.Add(value);
                }),
            out _);

        host.Activate();
        _ui.PumpUntil(() => results.Count == 1); // well before the one-hour interval

        Assert.Equal([42], results);
        Assert.NotEqual(uiThread, workThread);
        Assert.Equal(uiThread, applyThread);
    }

    [Fact]
    public void Polling_can_wait_one_interval_before_its_first_run()
    {
        var runs = 0;
        var host = Host(
            scope => scope.Poll(Never, _ => Task.FromResult(Interlocked.Increment(ref runs)), _ => { }, runImmediately: false),
            out _);

        host.Activate();
        _ui.PumpFor(100);

        Assert.Equal(0, Volatile.Read(ref runs));
        host.Dispose();
    }

    [Fact]
    public void Polling_repeats_at_its_interval()
    {
        var results = new List<int>();
        var runs = 0;
        var host = Host(
            scope => scope.Poll(TimeSpan.FromMilliseconds(10), _ => Task.FromResult(Interlocked.Increment(ref runs)), results.Add),
            out _);

        host.Activate();
        _ui.PumpUntil(() => results.Count >= 3);

        Assert.Equal([1, 2, 3], results.Take(3));
        host.Dispose();
    }

    [Fact]
    public void A_failing_poll_is_reported_and_retried_without_faulting_the_widget()
    {
        var attempts = 0;
        var errors = new List<Exception>();
        var results = new List<string>();
        var host = Host(
            scope => scope.Poll(
                TimeSpan.FromMilliseconds(10),
                _ => Interlocked.Increment(ref attempts) == 1
                    ? Task.FromException<string>(new HttpRequestException("offline"))
                    : Task.FromResult("online"),
                results.Add,
                onError: errors.Add),
            out _);

        host.Activate();
        _ui.PumpUntil(() => results.Count >= 1);

        Assert.Equal("offline", Assert.Single(errors).Message);
        Assert.Equal("online", results[0]);
        Assert.Equal(WidgetState.Active, host.State);
        host.Dispose();
    }

    [Fact]
    public void Work_that_hangs_is_timed_out_and_never_stacked()
    {
        using var release = new ManualResetEventSlim();
        var started = 0;
        var errors = new List<Exception>();
        var results = new List<int>();
        var host = Host(
            scope => scope.Poll(
                TimeSpan.FromMilliseconds(10),
                _ =>
                {
                    // Blocks synchronously and ignores cancellation: the worst case.
                    var run = Interlocked.Increment(ref started);
                    if (run == 1)
                    {
                        release.Wait();
                    }

                    return Task.FromResult(run);
                },
                results.Add,
                timeout: TimeSpan.FromMilliseconds(50),
                onError: errors.Add),
            out _);

        host.Activate();
        _ui.PumpUntil(() => errors.Count >= 2); // the UI thread stayed free the whole time

        Assert.All(errors, e => Assert.IsType<TimeoutException>(e));
        Assert.Equal(1, Volatile.Read(ref started)); // no second run on top of the blocked one
        Assert.Empty(results);
        Assert.Equal(WidgetState.Active, host.State);

        release.Set();
        _ui.PumpUntil(() => results.Count >= 1); // it recovers once the call returns
        host.Dispose();
    }

    [Fact]
    public void Results_that_arrive_after_deactivation_are_dropped()
    {
        var work = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ManualResetEventSlim();
        var applied = 0;
        var host = Host(
            scope => scope.Poll(
                Never,
                _ =>
                {
                    started.Set();
                    return work.Task;
                },
                _ => applied++),
            out _);
        host.Activate();
        Assert.True(started.Wait(5000, TestContext.Current.CancellationToken));

        host.Deactivate();
        work.SetResult(1);
        _ui.PumpFor(100);

        Assert.Equal(0, applied);
        Assert.Equal(WidgetState.Ready, host.State);
    }

    [Fact]
    public async Task Background_work_receives_a_token_that_is_cancelled_on_deactivation()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ManualResetEventSlim();
        var host = Host(
            scope => scope.Run(
                async token =>
                {
                    started.Set();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        observed.TrySetResult(true);
                        throw;
                    }

                    return 0;
                },
                _ => { },
                timeout: Never),
            out _);
        host.Activate();
        Assert.True(started.Wait(5000, TestContext.Current.CancellationToken));

        host.Deactivate();

        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_one_shot_run_delivers_its_result_on_the_ui_thread()
    {
        string? result = null;
        var host = Host(scope => scope.Run(_ => Task.FromResult("done"), value => result = value), out _);

        host.Activate();
        _ui.PumpUntil(() => result is not null);

        Assert.Equal("done", result);
    }

    [Fact]
    public void A_throwing_result_handler_faults_the_widget()
    {
        var host = Host(scope => scope.Run(_ => Task.FromResult(1), _ => throw new InvalidOperationException("bad render")), out var widget);

        host.Activate();
        _ui.PumpUntil(() => host.State == WidgetState.Faulted);

        Assert.Equal("bad render", host.LastError!.Message);
        Assert.True(widget.IsDisposed);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(3, 80)]
    public void Failures_back_off_exponentially(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), WidgetScope.NextDelay(TimeSpan.FromSeconds(10), failures));

    [Fact]
    public void Back_off_is_capped_but_never_shorter_than_the_interval()
    {
        Assert.Equal(WidgetScope.MaxBackoff, WidgetScope.NextDelay(TimeSpan.FromSeconds(10), 30));
        Assert.Equal(TimeSpan.FromMinutes(30), WidgetScope.NextDelay(TimeSpan.FromMinutes(30), 5));
    }

    private sealed class Subscription : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}

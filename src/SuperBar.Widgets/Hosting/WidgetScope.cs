using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;

namespace SuperBar.Widgets;

/// <summary>
/// The host's implementation of <see cref="IWidgetScope"/>. Timers and owned subscriptions are tracked and
/// released on <see cref="Dispose"/>; background work runs on the thread pool and only its results cross to the
/// UI thread, where they pass through the host's fault guard and are dropped once the scope has ended.
/// </summary>
internal sealed class WidgetScope : IWidgetScope, IDisposable
{
    public static readonly TimeSpan DefaultWorkTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Action<Action, string> _guard;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _owned = new();
    private volatile bool _disposed;

    public WidgetScope(IUiDispatcher dispatcher, ILogger logger, Action<Action, string> guard)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _guard = guard;
    }

    public CancellationToken Token => _cts.Token;

    public void Every(TimeSpan interval, Action tick, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        if (_disposed)
        {
            return;
        }

        var timer = _dispatcher.CreateTimer(interval, () => Invoke(tick, "timer tick"));
        _owned.Add(timer);
        timer.Start();
        if (immediate)
        {
            Invoke(tick, "timer tick");
        }
    }

    public void After(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
        {
            return;
        }

        if (delay < TimeSpan.FromMilliseconds(1))
        {
            delay = TimeSpan.FromMilliseconds(1);
        }

        IUiTimer? timer = null;
        timer = _dispatcher.CreateTimer(delay, () =>
        {
            // One shot: the timer is gone before the action runs, so the action may re-arm freely.
            timer!.Stop();
            timer.Dispose();
            _owned.Remove(timer);
            Invoke(action, "delayed action");
        });
        _owned.Add(timer);
        timer.Start();
    }

    public void Poll<T>(
        TimeSpan interval,
        Func<CancellationToken, Task<T>> work,
        Action<T> apply,
        TimeSpan? timeout = null,
        bool runImmediately = true,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        if (_disposed)
        {
            return;
        }

        var token = Token;
        _ = Task.Run(() => PollAsync(interval, work, apply, timeout ?? DefaultWorkTimeout, runImmediately, onError, token), CancellationToken.None);
    }

    public void Run<T>(Func<CancellationToken, Task<T>> work, Action<T> apply, TimeSpan? timeout = null, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(apply);
        if (_disposed)
        {
            return;
        }

        var token = Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var run = await RunWorkAsync(work, timeout ?? DefaultWorkTimeout, token).ConfigureAwait(false);
                    Report(run, apply, onError, failures: 1);
                }
                catch (OperationCanceledException)
                {
                    // The scope ended.
                }
            },
            CancellationToken.None);
    }

    public void Own(IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (_disposed)
        {
            subscription.Dispose();
            return;
        }

        _owned.Add(subscription);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex, "A cancellation callback of a widget failed");
        }

        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release a widget resource");
            }
        }

        _owned.Clear();
    }

    /// <summary>The wait before the next run: the interval, doubled per consecutive failure up to <see cref="MaxBackoff"/>.</summary>
    internal static TimeSpan NextDelay(TimeSpan interval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return interval;
        }

        var backoff = interval.TotalMilliseconds * Math.Pow(2, Math.Min(consecutiveFailures, 16));
        var capped = Math.Min(backoff, MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(interval.TotalMilliseconds, capped));
    }

    private async Task PollAsync<T>(
        TimeSpan interval,
        Func<CancellationToken, Task<T>> work,
        Action<T> apply,
        TimeSpan timeout,
        bool runImmediately,
        Action<Exception>? onError,
        CancellationToken token)
    {
        try
        {
            if (!runImmediately)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }

            var failures = 0;
            Task? hung = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();

                WorkResult<T> run;
                if (hung is { IsCompleted: false })
                {
                    // Never stack runs on top of one that ignores cancellation: it would drain the thread pool.
                    run = new WorkResult<T>(default, new TimeoutException("The previous run is still blocked."), hung);
                }
                else
                {
                    run = await RunWorkAsync(work, timeout, token).ConfigureAwait(false);
                }

                hung = run.Hung;
                failures = run.Error is null ? 0 : failures + 1;
                Report(run, apply, onError, failures);

                await Task.Delay(NextDelay(interval, failures), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The scope ended.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A widget's polling loop stopped unexpectedly");
        }
    }

    private static async Task<WorkResult<T>> RunWorkAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken scopeToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(scopeToken);

        // Task.Run so that work which blocks synchronously (no await) can still be timed out and abandoned.
        var task = Task.Run(() => work(cts.Token), cts.Token);
        _ = task.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // observed: an abandoned run must not surface as an unobserved task exception
                ((CancellationTokenSource)state!).Dispose();
            },
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            return new WorkResult<T>(await task.WaitAsync(timeout, scopeToken).ConfigureAwait(false), null, null);
        }
        catch (TimeoutException)
        {
            try
            {
                cts.Cancel(); // ask it to stop, but do not wait for it
            }
            catch (ObjectDisposedException)
            {
                // It completed in the meantime.
            }

            return new WorkResult<T>(default, new TimeoutException($"The operation did not complete within {timeout.TotalSeconds:0.##} s."), task);
        }
        catch (OperationCanceledException) when (scopeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WorkResult<T>(default, ex, null);
        }
    }

    private void Report<T>(WorkResult<T> run, Action<T> apply, Action<Exception>? onError, int failures)
    {
        if (run.Error is null)
        {
            var value = run.Value!;
            Post(() => apply(value), "background result");
            return;
        }

        _logger.LogWarning(run.Error, "Background work of a widget failed ({Failures} in a row)", failures);
        if (onError is not null)
        {
            var error = run.Error;
            Post(() => onError(error), "background error");
        }
    }

    private void Post(Action action, string operation)
    {
        if (!_disposed)
        {
            _dispatcher.TryEnqueue(() => Invoke(action, operation));
        }
    }

    private void Invoke(Action action, string operation)
    {
        if (!_disposed)
        {
            _guard(action, operation);
        }
    }

    private readonly record struct WorkResult<T>(T? Value, Exception? Error, Task? Hung);
}

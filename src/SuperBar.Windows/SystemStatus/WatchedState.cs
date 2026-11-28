using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;

namespace SuperBar.Windows.SystemStatus;

/// <summary>
/// A piece of Windows state that is followed only while someone listens. The first <see cref="Changed"/> subscriber
/// attaches the Windows event handlers (<see cref="Attach"/>) and the last one to leave detaches them, so nothing is
/// subscribed while the bar is hidden or suspended by a full-screen app.
/// <para>
/// Windows raises its events on thread-pool threads, often in bursts (a Wi-Fi reconnect fires a dozen). Each one only
/// asks for a read (<see cref="RequestRead"/>); requests arriving while a read is already pending are folded into it.
/// The read runs on the thread pool and its result is published on the UI thread, where <see cref="Changed"/> is
/// raised only when the state really changed.
/// </para>
/// </summary>
internal abstract class WatchedState<T> : IDisposable
    where T : class
{
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _settle;
    private EventHandler<T>? _changed;
    private int _readPending;
    private int _readSequence;
    private bool _attached;
    private bool _disposed;

    protected WatchedState(IUiDispatcher dispatcher, ILogger logger, T initial, TimeSpan settle)
    {
        _dispatcher = dispatcher;
        Logger = logger;
        Current = initial;
        _settle = settle;
    }

    /// <summary>The last published state. Kept while nobody listens, so the next subscriber starts from it.</summary>
    public T Current { get; private set; }

    /// <summary>Raised on the UI thread. Subscribe and unsubscribe on the UI thread.</summary>
    public event EventHandler<T>? Changed
    {
        add
        {
            var first = _changed is null;
            _changed += value;
            if (first && _changed is not null && !_disposed)
            {
                Start();
            }
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
            {
                Stop();
            }
        }
    }

    protected ILogger Logger { get; }

    /// <summary>Subscribes to the Windows events. Called on the UI thread.</summary>
    protected abstract void Attach();

    /// <summary>Unsubscribes from the Windows events. Called on the UI thread.</summary>
    protected abstract void Detach();

    /// <summary>Reads the current state. Runs on the thread pool; may throw (the read is then dropped and logged).</summary>
    protected abstract T Read();

    /// <summary>Asks for a fresh read after the settle delay. Safe from any thread.</summary>
    protected void RequestRead()
    {
        if (!_attached || Interlocked.Exchange(ref _readPending, 1) == 1)
        {
            return;
        }

        _ = ReadSoonAsync();
    }

    /// <summary>Publishes a state read on the UI thread right now (used for a synchronous first read).</summary>
    protected void PublishNow(T state)
    {
        if (!EqualityComparer<T>.Default.Equals(state, Current))
        {
            Current = state;
            _changed?.Invoke(this, state);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _changed = null;
        Stop();
    }

    private void Start()
    {
        if (_attached)
        {
            return;
        }

        try
        {
            _attached = true;
            Attach();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not start watching {State}", typeof(T).Name);
        }

        Interlocked.Exchange(ref _readPending, 0);
        RequestRead();
    }

    private void Stop()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        try
        {
            Detach();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not stop watching {State}", typeof(T).Name);
        }
    }

    private async Task ReadSoonAsync()
    {
        await Task.Delay(_settle).ConfigureAwait(false);

        // Cleared before reading, so an event arriving during the read asks for another one.
        Interlocked.Exchange(ref _readPending, 0);
        var sequence = Interlocked.Increment(ref _readSequence);
        T state;
        try
        {
            state = Read();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read {State}", typeof(T).Name);
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            // A slower, older read never overwrites a newer one.
            if (!_disposed && sequence == Volatile.Read(ref _readSequence))
            {
                PublishNow(state);
            }
        });
    }
}

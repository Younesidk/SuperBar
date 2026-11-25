namespace SuperBar.Core.Runtime;

/// <summary>
/// The UI thread, independent of the UI framework. Services marshal work onto it and create timers
/// that tick on it, which keeps view models and widgets free of framework types and testable headlessly.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller runs on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread. Returns false when the dispatcher is shutting down.</summary>
    bool TryEnqueue(Action action);

    /// <summary>Creates a stopped, repeating timer whose ticks run on the UI thread.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A repeating timer that ticks on the UI thread.</summary>
public interface IUiTimer : IDisposable
{
    /// <summary>The time between ticks. May be changed while running (e.g. from inside a tick).</summary>
    TimeSpan Interval { get; set; }

    bool IsRunning { get; }

    void Start();

    void Stop();
}

public static class UiDispatcherExtensions
{
    /// <summary>Throws unless called on the UI thread.</summary>
    public static void VerifyAccess(this IUiDispatcher dispatcher)
    {
        if (!dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("This operation must run on the UI thread.");
        }
    }

    /// <summary>Runs <paramref name="action"/> now when on the UI thread, otherwise queues it.</summary>
    public static void Invoke(this IUiDispatcher dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            dispatcher.TryEnqueue(action);
        }
    }
}

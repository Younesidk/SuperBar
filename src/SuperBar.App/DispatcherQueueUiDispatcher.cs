using Microsoft.UI.Dispatching;
using SuperBar.Core.Runtime;

namespace SuperBar.App;

/// <summary><see cref="IUiDispatcher"/> over the WinUI UI thread's <see cref="DispatcherQueue"/>.</summary>
internal sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public bool TryEnqueue(Action action) => queue.TryEnqueue(() => action());

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(queue.CreateTimer(), interval, tick);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly Action _tick;
        private bool _disposed;

        public Timer(DispatcherQueueTimer timer, TimeSpan interval, Action tick)
        {
            _timer = timer;
            _tick = tick;
            _timer.Interval = interval;
            _timer.IsRepeating = true;
            _timer.Tick += OnTick;
        }

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public bool IsRunning => _timer.IsRunning;

        public void Start()
        {
            if (!_disposed)
            {
                _timer.Start();
            }
        }

        public void Stop() => _timer.Stop();

        private void OnTick(DispatcherQueueTimer sender, object args) => _tick();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}

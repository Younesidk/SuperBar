using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Widgets.BuiltIn;

/// <summary>
/// Example widget validating the widget architecture: live CPU and memory usage.
/// Samples every 2 s only while active; no background threads. The widget is a controller plus a
/// <see cref="SystemResourcesModel"/>; its WinUI view lives in SuperBar.Runtime.
/// </summary>
public sealed class SystemResourcesWidget : IWidget
{
    public static readonly WidgetDescriptor Descriptor = new(
        DefaultSettings.SystemResourcesWidgetType,
        "System resources",
        "CPU and memory usage at a glance.",
        defaultWidth: 156,
        minWidth: 112,
        maxWidth: 280,
        factory: ctx => new SystemResourcesWidget(
            ctx.Services.GetRequiredService<ISystemMetricsProvider>(),
            ctx.Services.GetService<IUiDispatcher>(),
            ctx.Logger));

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly ISystemMetricsProvider _metrics;
    private readonly IUiTimer _timer;

    public SystemResourcesWidget(ISystemMetricsProvider metrics, IUiDispatcher? dispatcher, ILogger logger)
    {
        _metrics = metrics;
        if (dispatcher is null)
        {
            // Only happens without a UI (tests): sampling still works on Activate, it just does not repeat.
            logger.LogWarning("No UI dispatcher registered; system resources will not refresh periodically");
            _timer = new InertTimer(Interval);
        }
        else
        {
            _timer = dispatcher.CreateTimer(Interval, Refresh);
        }
    }

    public SystemResourcesModel Model { get; } = new();

    public object View => Model;

    public void Activate()
    {
        _metrics.Sample(); // prime the CPU delta
        Refresh();
        _timer.Start();
    }

    public void Deactivate() => _timer.Stop();

    public void ApplyOptions(IReadOnlyDictionary<string, string> options)
    {
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    private void Refresh() => Model.Update(_metrics.Sample());

    private sealed class InertTimer(TimeSpan interval) : IUiTimer
    {
        public TimeSpan Interval { get; set; } = interval;

        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Dispose() => IsRunning = false;
    }
}

/// <summary>Presentation state of <see cref="SystemResourcesWidget"/>: two meters (0–100) and a tooltip.</summary>
public sealed class SystemResourcesModel : ObservableObject
{
    private double _cpuPercent;
    private double _memoryPercent;
    private string _cpuText = "–";
    private string _memoryText = "–";
    private string _toolTip = "";

    public double CpuPercent { get => _cpuPercent; private set => SetProperty(ref _cpuPercent, value); }

    public double MemoryPercent { get => _memoryPercent; private set => SetProperty(ref _memoryPercent, value); }

    public string CpuText { get => _cpuText; private set => SetProperty(ref _cpuText, value); }

    public string MemoryText { get => _memoryText; private set => SetProperty(ref _memoryText, value); }

    public string ToolTip { get => _toolTip; private set => SetProperty(ref _toolTip, value); }

    public void Update(SystemResourceSnapshot sample)
    {
        CpuPercent = Math.Clamp(sample.CpuPercent, 0, 100);
        MemoryPercent = Math.Clamp(sample.MemoryPercent, 0, 100);
        CpuText = $"{CpuPercent:0}%";
        MemoryText = $"{MemoryPercent:0}%";
        ToolTip = $"CPU {sample.CpuPercent:0}%\nMemory {sample.MemoryUsedBytes / 1073741824.0:0.0} / {sample.MemoryTotalBytes / 1073741824.0:0.0} GB";
    }
}

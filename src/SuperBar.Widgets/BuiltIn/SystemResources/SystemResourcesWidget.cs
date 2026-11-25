using SuperBar.Core.Mvvm;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Widgets.BuiltIn;

/// <summary>
/// Live CPU and memory usage, sampled every 2 s while the widget is active. The sample is a pair of cheap,
/// non-blocking system calls, so it runs on the UI thread timer; its WinUI view lives in SuperBar.Runtime.
/// </summary>
[Widget(DefaultSettings.SystemResourcesWidgetType, "System resources",
    Description = "CPU and memory usage at a glance.",
    Glyph = "\uE9D9",
    DefaultWidth = 156,
    MinWidth = 112,
    MaxWidth = 280)]
public sealed class SystemResourcesWidget(WidgetContext context, ISystemMetricsProvider metrics) : WidgetBase(context)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public SystemResourcesModel Model { get; } = new();

    public override object ViewModel => Model;

    protected override void OnActivated(IWidgetScope scope)
    {
        metrics.Sample(); // prime the CPU delta
        scope.Every(Interval, () => Model.Update(metrics.Sample()), immediate: true);
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

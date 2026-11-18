using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Widgets.BuiltIn;

/// <summary>
/// Example widget validating the widget architecture: live CPU and memory usage.
/// Samples every 2 s only while active; no background threads.
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
        factory: ctx => new SystemResourcesWidget(ctx.Services.GetRequiredService<ISystemMetricsProvider>()));

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly ISystemMetricsProvider _metrics;
    private readonly DispatcherTimer _timer;
    private readonly Meter _cpu = new("CPU");
    private readonly Meter _memory = new("RAM");

    public SystemResourcesWidget(ISystemMetricsProvider metrics)
    {
        _metrics = metrics;
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Refresh());

        View = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Margin = new Thickness(12, 0),
            Children = { _cpu.Root, _memory.Root },
        };
    }

    public Control View { get; }

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

    public void Dispose() => _timer.Stop();

    private void Refresh()
    {
        var sample = _metrics.Sample();
        _cpu.Set(sample.CpuPercent);
        _memory.Set(sample.MemoryPercent);
        ToolTip.SetTip(View, $"CPU {sample.CpuPercent:0}%\nMemory {sample.MemoryUsedBytes / 1073741824.0:0.0} / {sample.MemoryTotalBytes / 1073741824.0:0.0} GB");
    }

    /// <summary>Label + thin bar + value. Styled by the taskbar theme via style classes.</summary>
    private sealed class Meter
    {
        private readonly TextBlock _value = new() { Classes = { "sb-text", "sb-meter-value" }, Text = "–", HorizontalAlignment = HorizontalAlignment.Right };
        private readonly ColumnDefinition _filled = new(new GridLength(0, GridUnitType.Star));
        private readonly ColumnDefinition _empty = new(new GridLength(100, GridUnitType.Star));

        public Meter(string label)
        {
            var bar = new Grid
            {
                Height = 4,
                VerticalAlignment = VerticalAlignment.Center,
                ColumnDefinitions = { _filled, _empty },
                Children =
                {
                    new Border { Classes = { "sb-meter-track" }, [Grid.ColumnSpanProperty] = 2 },
                    new Border { Classes = { "sb-meter-fill" } },
                },
            };

            Root = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("30,*,36"),
                Children =
                {
                    new TextBlock { Classes = { "sb-subtle", "sb-meter-label" }, Text = label, VerticalAlignment = VerticalAlignment.Center },
                    bar,
                    _value,
                },
            };
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(_value, 2);
        }

        public Grid Root { get; }

        public void Set(double percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            _filled.Width = new GridLength(percent, GridUnitType.Star);
            _empty.Width = new GridLength(100 - percent, GridUnitType.Star);
            _value.Text = $"{percent:0}%";
        }
    }
}

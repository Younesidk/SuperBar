using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using SuperBar.Runtime.Theming;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// View of <see cref="SystemResourcesWidget"/>: CPU and RAM rows, each a 30 px label, a 4 px bar split into
/// filled/empty star columns, and a right-aligned 36 px tabular percentage. Brushes are the shared theme brushes,
/// so theme and accent changes apply live.
/// </summary>
[WidgetViewFor(typeof(SystemResourcesModel))]
public sealed partial class SystemResourcesView : WidgetViewBase
{
    private readonly SystemResourcesModel _model;
    private readonly Meter _cpu;
    private readonly Meter _memory;

    public SystemResourcesView(SystemResourcesModel model, TaskbarTheme theme)
        : base(model)
    {
        _model = model;
        _cpu = new Meter("CPU", theme);
        _memory = new Meter("RAM", theme);
        Content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Margin = new Thickness(12, 0, 12, 0),
            Children = { _cpu.Root, _memory.Root },
        };

        Refresh();
    }

    protected override void Refresh()
    {
        _cpu.Set(_model.CpuPercent, _model.CpuText);
        _memory.Set(_model.MemoryPercent, _model.MemoryText);
        ToolTipService.SetToolTip(this, string.IsNullOrEmpty(_model.ToolTip) ? null : _model.ToolTip);
    }

    private sealed class Meter
    {
        private readonly TextBlock _value;
        private readonly ColumnDefinition _filled = new() { Width = new GridLength(0, GridUnitType.Star) };
        private readonly ColumnDefinition _empty = new() { Width = new GridLength(100, GridUnitType.Star) };

        public Meter(string label, TaskbarTheme theme)
        {
            _value = new TextBlock
            {
                Text = "–",
                FontSize = 11,
                Foreground = theme.Foreground,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular); // was FontFeatures="+tnum"

            var track = new Border { Background = theme.Track, CornerRadius = new CornerRadius(2) };
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { Background = theme.Accent, CornerRadius = new CornerRadius(2) };

            var bar = new Grid
            {
                Height = 4,
                VerticalAlignment = VerticalAlignment.Center,
                ColumnDefinitions = { _filled, _empty },
                Children = { track, fill },
            };

            var name = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = theme.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Root = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(30) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(36) },
                },
                Children = { name, bar, _value },
            };
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(_value, 2);
        }

        public Grid Root { get; }

        public void Set(double percent, string text)
        {
            percent = Math.Clamp(percent, 0, 100);
            _filled.Width = new GridLength(percent, GridUnitType.Star);
            _empty.Width = new GridLength(100 - percent, GridUnitType.Star);
            _value.Text = text;
        }
    }
}

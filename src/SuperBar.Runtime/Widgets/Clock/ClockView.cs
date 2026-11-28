using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SuperBar.Runtime.Theming;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// View of <see cref="ClockWidget"/>: a bar button showing the time over the date. It uses the bar's own button
/// style, so it hovers, presses and follows the theme exactly like Start and the system section.
/// </summary>
[WidgetViewFor(typeof(ClockModel))]
public sealed partial class ClockView : WidgetViewBase
{
    private readonly ClockModel _model;
    private readonly Button _button;
    private readonly TextBlock _time;
    private readonly TextBlock _date;

    public ClockView(ClockModel model, TaskbarTheme theme)
        : base(model)
    {
        _model = model;
        _time = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.Foreground,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _date = new TextBlock
        {
            FontSize = 11,
            Foreground = theme.Subtle,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        _button = new Button
        {
            Command = model.Open,

            // Shell flyouts are toggled on press, like the other system buttons (see ShellCommands).
            ClickMode = ClickMode.Press,
            Content = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 0,
                Children = { _time, _date },
            },
        };
        if (theme.FindStyle(TaskbarTheme.WideButtonStyleKey) is { } style)
        {
            _button.Style = style;
        }

        AutomationProperties.SetName(_button, "Clock");
        Content = _button;
        Refresh();
    }

    protected override void Refresh()
    {
        _time.Text = _model.Time;
        _date.Text = _model.Date;
        _date.Visibility = _model.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_button, string.IsNullOrEmpty(_model.ToolTip) ? null : _model.ToolTip);
    }
}

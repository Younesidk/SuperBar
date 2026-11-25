using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using SuperBar.Runtime.Theming;
using SuperBar.Widgets;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Runtime.Widgets;

/// <summary>Creates the WinUI view for a widget presentation model of type <see cref="ModelType"/>.</summary>
public sealed record WidgetViewRegistration(Type ModelType, Func<IServiceProvider, object, UIElement> Factory);

public static class WidgetViewServiceCollectionExtensions
{
    /// <summary>Registers the view used for widgets whose <see cref="IWidget.View"/> is a <typeparamref name="TModel"/>.</summary>
    public static IServiceCollection AddWidgetView<TModel>(this IServiceCollection services, Func<IServiceProvider, TModel, UIElement> factory)
        where TModel : class
    {
        services.AddSingleton(new WidgetViewRegistration(typeof(TModel), (sp, model) => factory(sp, (TModel)model)));
        return services;
    }
}

/// <summary>
/// Turns what a widget exposes as its <see cref="IWidget.View"/> into a UI element for the taskbar:
/// UI elements pass through, presentation models get their registered view, and fault placeholders become
/// a subtle "Widget error" label. UI thread only.
/// </summary>
public sealed class WidgetViewResolver(IEnumerable<WidgetViewRegistration> registrations, IServiceProvider services, TaskbarTheme theme, ILogger<WidgetViewResolver> logger)
{
    private readonly Dictionary<Type, WidgetViewRegistration> _registrations = registrations.ToDictionary(r => r.ModelType);

    public UIElement Resolve(object view)
    {
        switch (view)
        {
            case UIElement element:
                return element;
            case WidgetFaultPlaceholder fault:
                return Label(fault.Message);
        }

        for (var type = view.GetType(); type is not null; type = type.BaseType)
        {
            if (_registrations.TryGetValue(type, out var registration))
            {
                try
                {
                    return registration.Factory(services, view);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create the view for widget model {Model}", view.GetType().Name);
                    return Label("Widget error");
                }
            }
        }

        logger.LogWarning("No view registered for widget model {Model}", view.GetType().FullName);
        return Label("Widget error");
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = theme.Subtle,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
}

/// <summary>
/// View of <see cref="SystemResourcesWidget"/>: CPU and RAM rows, each a 30 px label, a 4 px bar split into
/// filled/empty star columns, and a right-aligned 36 px tabular percentage. Brushes are the shared theme brushes,
/// so theme and accent changes apply live.
/// </summary>
public sealed partial class SystemResourcesView : UserControl
{
    private readonly SystemResourcesModel _model;
    private readonly Meter _cpu;
    private readonly Meter _memory;

    public SystemResourcesView(SystemResourcesModel model, TaskbarTheme theme)
    {
        _model = model;
        _cpu = new Meter("CPU", theme);
        _memory = new Meter("RAM", theme);
        IsTabStop = false;
        Content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Margin = new Thickness(12, 0, 12, 0),
            Children = { _cpu.Root, _memory.Root },
        };

        Refresh();
        model.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
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

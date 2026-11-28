using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SuperBar.Runtime.Theming;
using SuperBar.Widgets;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// Marks the WinUI view of a widget presentation model. Every view carrying it is discovered at start-up
/// (<c>AddWidgetViewsFrom</c>) and created with constructor injection: the model is supplied, everything else
/// (for example <see cref="TaskbarTheme"/>) comes from the container. Derive views from <see cref="WidgetViewBase"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WidgetViewForAttribute(Type modelType) : Attribute
{
    public Type ModelType { get; } = modelType;
}

/// <summary>Creates the WinUI view for a widget presentation model of type <see cref="ModelType"/>.</summary>
public sealed record WidgetViewRegistration(Type ModelType, Func<IServiceProvider, object, UIElement> Factory);

public static class WidgetViewServiceCollectionExtensions
{
    /// <summary>
    /// Discovers every class marked with <see cref="WidgetViewForAttribute"/> in <paramref name="assembly"/>.
    /// Only types are inspected here; the views themselves are created later, on the UI thread.
    /// </summary>
    public static IServiceCollection AddWidgetViewsFrom(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<WidgetViewForAttribute>() is not { } attribute)
            {
                continue;
            }

            if (type.IsAbstract || !typeof(UIElement).IsAssignableFrom(type))
            {
                throw new InvalidOperationException($"[WidgetViewFor] type {type.FullName} must be a non-abstract UIElement.");
            }

            var viewType = type;
            services.AddSingleton(new WidgetViewRegistration(
                attribute.ModelType,
                (sp, model) => (UIElement)ActivatorUtilities.CreateInstance(sp, viewType, model)));
        }

        return services;
    }

    /// <summary>Registers a view by hand, for a view that needs a custom factory.</summary>
    public static IServiceCollection AddWidgetView<TModel>(this IServiceCollection services, Func<IServiceProvider, TModel, UIElement> factory)
        where TModel : class
    {
        services.AddSingleton(new WidgetViewRegistration(typeof(TModel), (sp, model) => factory(sp, (TModel)model)));
        return services;
    }
}

/// <summary>
/// Turns what a <see cref="WidgetHost"/> exposes as its <see cref="WidgetHost.View"/> into a UI element for the
/// taskbar: presentation models get their registered view, UI elements pass through, and fault placeholders
/// become a subtle "Widget error" label that restarts the widget on click. UI thread only.
/// </summary>
public sealed class WidgetViewResolver(IEnumerable<WidgetViewRegistration> registrations, IServiceProvider services, TaskbarTheme theme, ILogger<WidgetViewResolver> logger)
{
    private readonly Dictionary<Type, WidgetViewRegistration> _registrations = Index(registrations);

    public UIElement Resolve(object view)
    {
        switch (view)
        {
            case UIElement element:
                return element;
            case WidgetFaultPlaceholder fault:
                return FaultLabel(fault);
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

        logger.LogWarning("No view registered for widget model {Model}; mark its view with [WidgetViewFor]", view.GetType().FullName);
        return Label("Widget error");
    }

    private static Dictionary<Type, WidgetViewRegistration> Index(IEnumerable<WidgetViewRegistration> registrations)
    {
        var index = new Dictionary<Type, WidgetViewRegistration>();
        foreach (var registration in registrations)
        {
            if (!index.TryAdd(registration.ModelType, registration))
            {
                throw new InvalidOperationException($"More than one widget view is registered for {registration.ModelType.FullName}.");
            }
        }

        return index;
    }

    private TextBlock FaultLabel(WidgetFaultPlaceholder fault)
    {
        var label = Label(fault.Message);
        label.Margin = new Thickness(10, 0, 10, 0);
        ToolTipService.SetToolTip(label, fault.ToolTip);
        label.Tapped += (_, e) =>
        {
            e.Handled = true;
            fault.Restart();
        };
        return label;
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

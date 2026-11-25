using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SuperBar.Widgets;

public static class WidgetsServiceCollectionExtensions
{
    /// <summary>Registers the widget registry and every built-in widget type.</summary>
    public static IServiceCollection AddSuperBarWidgets(this IServiceCollection services)
    {
        services.AddSingleton<IWidgetRegistry, WidgetRegistry>();
        services.AddWidgetsFrom(typeof(WidgetHost).Assembly);
        return services;
    }

    /// <summary>
    /// Discovers every class marked with <see cref="WidgetAttribute"/> in <paramref name="assembly"/>.
    /// A new widget needs no registration code: adding the class is enough.
    /// </summary>
    public static IServiceCollection AddWidgetsFrom(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsDefined(typeof(WidgetAttribute), inherit: false))
            {
                services.AddWidget(WidgetDescriptor.FromType(type));
            }
        }

        return services;
    }

    /// <summary>Registers a hand-built widget type.</summary>
    public static IServiceCollection AddWidget(this IServiceCollection services, WidgetDescriptor descriptor)
    {
        services.AddSingleton(descriptor);
        return services;
    }
}

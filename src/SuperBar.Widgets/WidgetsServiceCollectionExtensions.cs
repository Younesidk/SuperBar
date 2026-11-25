using Microsoft.Extensions.DependencyInjection;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Widgets;

public static class WidgetsServiceCollectionExtensions
{
    /// <summary>Registers the widget registry and the built-in widget types.</summary>
    public static IServiceCollection AddSuperBarWidgets(this IServiceCollection services)
    {
        services.AddSingleton<IWidgetRegistry, WidgetRegistry>();
        services.AddWidget(SystemResourcesWidget.Descriptor);
        return services;
    }

    /// <summary>Registration point for additional widget types (built-in or, later, from plug-ins).</summary>
    public static IServiceCollection AddWidget(this IServiceCollection services, WidgetDescriptor descriptor)
    {
        services.AddSingleton(descriptor);
        return services;
    }
}

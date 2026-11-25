using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Runtime;
using SuperBar.Runtime.Services;
using SuperBar.Runtime.Theming;
using SuperBar.Runtime.Widgets;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Runtime;

public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the taskbar runtime. Its UI services (theme brushes, icon bitmaps, widget views) are created lazily
    /// on the UI thread by <see cref="TaskbarRuntime"/>.
    /// </summary>
    public static IServiceCollection AddSuperBarRuntime(this IServiceCollection services)
    {
        services.AddSingleton<IconCache>();
        services.AddSingleton<TaskbarTheme>();
        services.AddSingleton<WidgetViewResolver>();
        services.AddSingleton<TaskbarRuntime>();
        services.AddSingleton<ITaskbarRuntimeController>(sp => sp.GetRequiredService<TaskbarRuntime>());

        // Views for the built-in widgets' presentation models.
        services.AddWidgetView<SystemResourcesModel>((sp, model) => new SystemResourcesView(model, sp.GetRequiredService<TaskbarTheme>()));
        return services;
    }
}

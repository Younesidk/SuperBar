using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Runtime;
using SuperBar.Runtime.Services;

namespace SuperBar.Runtime;

public static class RuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddSuperBarRuntime(this IServiceCollection services)
    {
        services.AddSingleton<IconCache>();
        services.AddSingleton<TaskbarRuntime>();
        services.AddSingleton<ITaskbarRuntimeController>(sp => sp.GetRequiredService<TaskbarRuntime>());
        return services;
    }
}

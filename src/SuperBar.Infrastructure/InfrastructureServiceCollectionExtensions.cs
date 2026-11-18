using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Infrastructure.Persistence;

namespace SuperBar.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddSuperBarInfrastructure(this IServiceCollection services, AppPaths paths, Serilog.ILogger logger)
    {
        services.AddSingleton<IAppPaths>(paths);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new SerilogLoggerProvider(logger, dispose: false));
        });

        services.AddSingleton<ISettingsStore>(_ => new SqliteSettingsStore(paths.DatabasePath));
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ITaskbarRecoveryStore>(_ => new JsonTaskbarRecoveryStore(paths.RecoveryFilePath));
        return services;
    }
}

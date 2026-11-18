using Serilog;
using Serilog.Events;

namespace SuperBar.Infrastructure.Logging;

public static class LoggingConfiguration
{
    /// <summary>Creates the process-wide Serilog logger: compact rolling files under %LOCALAPPDATA%\SuperBar\logs.</summary>
    public static Serilog.ILogger CreateLogger(string logDirectory, bool verbose = false)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(logDirectory, "superbar-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}

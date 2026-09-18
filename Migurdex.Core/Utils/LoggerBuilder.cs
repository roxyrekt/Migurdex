using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace Migurdex.Core.Utils;

public static class LoggerBuilder
{
    public static void ConfigureLogging(IServiceCollection services, IConfiguration configuration)
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            var exeDir = Environment.ProcessPath is { Length: > 0 } p ? Path.GetDirectoryName(p) : null;
            baseDir = !string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir)
                          ? exeDir!
                          : Directory.GetCurrentDirectory();
        }

        var     logsDir = Path.Combine(baseDir, "logs");
        string? logFile = null;
        try
        {
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            logFile = Path.Combine(logsDir, "Migurdex-.log");
        }
        catch
        {
            logFile = null;
        }

        var loggerConfig = new LoggerConfiguration()
#if DEBUG
                           .MinimumLevel.Debug()
#else
                               .MinimumLevel.Information()
#endif
                           .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                           .MinimumLevel.Override("System", LogEventLevel.Warning)
                           .Enrich.FromLogContext()
                           .WriteTo.Async(a => a.Console(
                                              theme: AnsiConsoleTheme.Code,
                                              outputTemplate:
                                              "[{Timestamp:MM/dd HH:mm:ss}] | {Level} | {SourceContext} {NewLine}{Message:lj}{NewLine}{Exception}{NewLine}"));

        if (logFile is not null)
        {
            loggerConfig.WriteTo.Async(a => a.File(
                                           logFile,
                                           rollingInterval: RollingInterval.Day,
                                           outputTemplate:
                                           "[{Timestamp:yyyy/MM/dd HH:mm:ss.fff}] | {Level} | {SourceContext} {NewLine}{Message:lj}{NewLine}{Exception}{NewLine}"));
        }

        var logger = loggerConfig.CreateLogger();

        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(logger, true);
        });

        Log.Logger = logger;
    }
}

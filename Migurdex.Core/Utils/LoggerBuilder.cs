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
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsDir))
        {
            Directory.CreateDirectory(logsDir);
        }

        var logger = new LoggerConfiguration()
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
                                        "[{Timestamp:MM/dd HH:mm:ss}] | {Level} | {SourceContext} {NewLine}{Message:lj}{NewLine}{Exception}{NewLine}"))
                     .WriteTo.Async(a => a.File(
                                        Path.Combine(logsDir, "Migurdex-.log"),
                                        rollingInterval: RollingInterval.Day,
                                        outputTemplate:
                                        "[{Timestamp:yyyy/MM/dd HH:mm:ss.fff}] | {Level} | {SourceContext} {NewLine}{Message:lj}{NewLine}{Exception}{NewLine}"))
                     .CreateLogger();

        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(logger, true);
        });

        Log.Logger = logger;
    }
}

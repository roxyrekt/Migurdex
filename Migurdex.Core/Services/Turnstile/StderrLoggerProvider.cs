using Microsoft.Extensions.Logging;

namespace Migurdex.Core.Services.Turnstile;

public sealed class StderrLoggerProvider : ILoggerProvider
{
    public bool Verbose { get; set; }

    public ILogger CreateLogger(string categoryName)
    {
        return new StderrLogger(this, categoryName);
    }

    public void Dispose() { }

    private sealed class StderrLogger(StderrLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= (provider.Verbose ? LogLevel.Debug : LogLevel.Warning);
        }

        public void Log<TState>(LogLevel     logLevel,
            EventId                          eventId,
            TState                           state,
            Exception?                       exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                Console.Error.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}");
                if (exception is not null)
                {
                    Console.Error.WriteLine(exception.ToString());
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}

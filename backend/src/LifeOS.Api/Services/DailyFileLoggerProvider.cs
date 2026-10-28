using Microsoft.Extensions.Logging;

namespace LifeOS.Api.Services;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _writeLock = new();

    public DailyFileLoggerProvider(string directory) => _directory = directory;

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (_writeLock)
        {
            var timestamp = DateTimeOffset.Now;
            var line = $"{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {category}" +
                (eventId.Id == 0 ? string.Empty : $" (EventId: {eventId.Id})") + $": {message}{Environment.NewLine}" +
                (exception is null ? string.Empty : exception + Environment.NewLine);

            try
            {
                Append(_directory, timestamp, line);
            }
            catch (IOException)
            {
                Append(FallbackDirectory, timestamp, line);
            }
            catch (UnauthorizedAccessException)
            {
                Append(FallbackDirectory, timestamp, line);
            }
        }
    }

    private static string FallbackDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LifeOS", "logs");

    private static void Append(string directory, DateTimeOffset timestamp, string line)
    {
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, $"lifeos-{timestamp:yyyy-MM-dd}.log"), line);
    }

    private sealed class DailyFileLogger(DailyFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

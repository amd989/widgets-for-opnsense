using Microsoft.Extensions.Logging;

namespace OPNsenseWidgets.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;

    public FileLoggerProvider(string filePath) => _filePath = filePath;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_filePath);

    public void Dispose() { }

    private sealed class FileLogger(string filePath) : ILogger
    {
        private static readonly object FileLock = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            lock (FileLock)
            {
                try { File.AppendAllText(filePath, line + Environment.NewLine); }
                catch { }
            }
        }
    }
}

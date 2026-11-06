using Microsoft.Extensions.Logging;

namespace OPNsenseWidgets.Logging;

/// <summary>
/// Appends log lines to a file. When the file passes <paramref name="maxBytes"/>,
/// it is moved to "*.old" (replacing the previous one) so the log never grows without bound.
/// </summary>
public sealed class FileLoggerProvider(string filePath, LogLevel minLevel = LogLevel.Debug, long maxBytes = 1_000_000)
    : ILoggerProvider
{
    private readonly FileSink _sink = new(filePath, maxBytes);

    public ILogger CreateLogger(string categoryName) => new FileLogger(_sink, minLevel);

    public void Dispose() { }

    private sealed class FileSink
    {
        private const int CheckEvery = 200;
        private readonly object _lock = new();
        private readonly string _path;
        private readonly long _maxBytes;
        private int _writesSinceCheck = CheckEvery; // check on the first write

        public FileSink(string path, long maxBytes)
        {
            _path = path;
            _maxBytes = maxBytes;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }
        }

        public void Write(string line)
        {
            lock (_lock)
            {
                try
                {
                    if (++_writesSinceCheck >= CheckEvery)
                    {
                        _writesSinceCheck = 0;
                        var info = new FileInfo(_path);
                        if (info.Exists && info.Length > _maxBytes)
                            File.Move(_path, _path + ".old", overwrite: true);
                    }
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    private sealed class FileLogger(FileSink sink, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            sink.Write(line);
        }
    }
}

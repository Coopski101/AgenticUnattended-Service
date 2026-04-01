using Microsoft.Extensions.Logging;

namespace AgenticUnattended.Tray;

public sealed class FileLogSink : IDisposable
{
    private readonly Lock _lock = new();
    private readonly StreamWriter _writer;

    public FileLogSink()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticUnattended", "logs");
        Directory.CreateDirectory(logDir);

        var logFile = Path.Combine(logDir, $"service-{DateTime.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(logFile, append: true) { AutoFlush = true };
    }

    public void Write(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine(message);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}

public sealed class FileLogSinkProvider : ILoggerProvider
{
    private readonly FileLogSink _sink;

    public FileLogSinkProvider(FileLogSink sink) => _sink = sink;

    public ILogger CreateLogger(string categoryName) => new FileLogSinkLogger(_sink, categoryName);

    public void Dispose() { }
}

public sealed class FileLogSinkLogger : ILogger
{
    private readonly FileLogSink _sink;
    private readonly string _category;

    public FileLogSinkLogger(FileLogSink sink, string category)
    {
        _sink = sink;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "????",
        };

        var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {_category}: {formatter(state, exception)}";
        if (exception is not null)
            msg += $"\n  {exception}";
        _sink.Write(msg);
    }
}

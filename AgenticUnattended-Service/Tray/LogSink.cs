using Microsoft.Extensions.Logging;

namespace AgenticUnattended.Tray;

public sealed class LogSink
{
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];
    private const int MaxLines = 5000;

    public event Action<string>? LogReceived;

    public void Write(string message)
    {
        Console.WriteLine(message);
        lock (_lock)
        {
            _lines.Add(message);
            if (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
        }
        LogReceived?.Invoke(message);
    }

    public string GetFullLog()
    {
        lock (_lock)
        {
            return string.Join("\n", _lines);
        }
    }
}

public sealed class LogSinkProvider : ILoggerProvider
{
    private readonly LogSink _sink;

    public LogSinkProvider(LogSink sink) => _sink = sink;

    public ILogger CreateLogger(string categoryName) => new LogSinkLogger(_sink, categoryName);

    public void Dispose() { }
}

public sealed class LogSinkLogger : ILogger
{
    private readonly LogSink _sink;
    private readonly string _category;

    public LogSinkLogger(LogSink sink, string category)
    {
        _sink = sink;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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

        var shortCategory = _category;
        var lastDot = _category.LastIndexOf('.');
        if (lastDot >= 0)
            shortCategory = _category[(lastDot + 1)..];

        var msg = $"[{DateTime.Now:HH:mm:ss}] {level} {shortCategory}: {formatter(state, exception)}";
        if (exception is not null)
            msg += $"\n  {exception.Message}";
        _sink.Write(msg);
    }
}

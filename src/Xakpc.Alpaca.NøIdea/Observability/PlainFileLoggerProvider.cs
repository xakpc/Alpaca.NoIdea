using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Observability;

/// <summary>Writes the ordinary application log to one plain text file.</summary>
public sealed class PlainFileLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public PlainFileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Write(
        LogLevel level,
        EventId eventId,
        string category,
        string message,
        Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Write(DateTimeOffset.UtcNow.ToString("O"));
            _writer.Write(' ');
            _writer.Write(level.ToString().ToUpperInvariant());
            _writer.Write(' ');
            _writer.Write(category);

            if (eventId.Id != 0 || eventId.Name is not null)
            {
                _writer.Write(" [");
                _writer.Write(eventId.Id);
                if (eventId.Name is not null)
                {
                    _writer.Write(':');
                    _writer.Write(eventId.Name);
                }
                _writer.Write(']');
            }

            _writer.Write(" - ");
            _writer.WriteLine(message);

            if (exception is not null)
            {
                _writer.WriteLine(exception);
            }
        }
    }

    private sealed class FileLogger(PlainFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, eventId, category, formatter(state, exception), exception);
            }
        }
    }
}

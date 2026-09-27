using Microsoft.Extensions.Logging;

namespace DeskPair.Core.Logging;

/// <summary>Appends single-line log records to a file (the service has no console); rotates at 8 MiB.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long RotateAt = 8 * 1024 * 1024;

    private readonly string _path;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string path)
    {
        _path = path;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                if (_writer is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }

                _writer.WriteLine(line);
                if (_writer.BaseStream.Length > RotateAt)
                {
                    _writer.Dispose();
                    _writer = null;
                    File.Move(_path, _path + ".1", overwrite: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel switch { LogLevel.Trace => "trce", LogLevel.Debug => "dbug", LogLevel.Information => "info", LogLevel.Warning => "warn", LogLevel.Error => "fail", _ => "crit" }}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }
    }
}

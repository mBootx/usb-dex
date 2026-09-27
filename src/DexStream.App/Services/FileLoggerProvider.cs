using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DexStream.App.Services;

/// <summary>
/// Writes the diagnostic log to a file on a background thread.
/// </summary>
/// <remarks>
/// A file log is what makes a support report possible: the UI's pane holds the last few hundred lines,
/// but a failure that happens during startup, or after the pane has scrolled, is only recoverable from
/// disk. Writes are queued so that logging from the video thread never blocks on file I/O.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Rotate once the file passes this size, keeping one previous generation.</summary>
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly Thread _writer;
    private readonly string _path;
    private bool _disposed;

    public FileLoggerProvider(string path)
    {
        _path = path;

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Rotate();
        }
        catch (Exception)
        {
            // A log that cannot be opened must not stop the app from running.
        }

        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "DexStream log writer",
            Priority = ThreadPriority.BelowNormal,
        };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Rotate()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxFileBytes)
        {
            return;
        }

        string previous = _path + ".1";
        File.Delete(previous);
        File.Move(_path, previous);
    }

    private void Enqueue(string line)
    {
        if (_disposed)
        {
            return;
        }

        // Dropping a line under extreme pressure is better than stalling the caller, which could be
        // the video thread.
        _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        try
        {
            using var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

            foreach (string line in _queue.GetConsumingEnumerable())
            {
                writer.WriteLine(line);

                // Flush whenever the queue drains, so the file is current whenever anything is idle
                // but a burst still gets batched.
                if (_queue.Count == 0)
                {
                    writer.Flush();
                }
            }

            writer.Flush();
        }
        catch (Exception)
        {
            // Nothing useful can be done: the log is the thing that failed.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;

            // Trim the namespace: the type name alone is what makes a log line readable.
            int lastDot = category.LastIndexOf('.');
            _category = lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder(160);
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ')
                .Append(Abbreviate(logLevel))
                .Append(' ')
                .Append(_category)
                .Append(": ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                line.AppendLine().Append(exception);
            }

            _provider.Enqueue(line.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}

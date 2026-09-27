using System.Collections.Concurrent;
using System.Globalization;
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

    private readonly BlockingCollection<LogEntry> _queue = new(new ConcurrentQueue<LogEntry>(), 4096);
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

    /// <summary>
    /// Waits until everything logged so far is on disk, or until <paramref name="timeout"/> passes.
    /// </summary>
    /// <remarks>
    /// Writes are asynchronous, which is right for normal operation but wrong for a fatal error: the
    /// process can end before the writer thread gets to the line explaining why. Call this before
    /// exiting or showing a crash dialog so that line is never the one that gets lost.
    /// </remarks>
    public void Flush(TimeSpan timeout)
    {
        if (_disposed)
        {
            return;
        }

        // Not disposed after the wait: on timeout the writer may still signal it later, and a
        // ManualResetEventSlim that never allocated a kernel handle needs no disposal anyway.
        var flushed = new ManualResetEventSlim(false);
        if (_queue.TryAdd(new LogEntry(null, flushed), timeout))
        {
            flushed.Wait(timeout);
        }
    }

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
        _queue.TryAdd(new LogEntry(line, null));
    }

    private void WriteLoop()
    {
        try
        {
            using var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

            foreach (LogEntry entry in _queue.GetConsumingEnumerable())
            {
                if (entry.Line is not null)
                {
                    writer.WriteLine(entry.Line);
                }

                // Flush whenever the queue drains, so the file is current whenever anything is idle
                // but a burst still gets batched, and always when someone is waiting on it.
                if (entry.Flushed is not null || _queue.Count == 0)
                {
                    writer.Flush();
                }

                entry.Flushed?.Set();
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

    /// <summary>A line to write, or — when <see cref="Line"/> is null — a request to flush and signal.</summary>
    private readonly record struct LogEntry(string? Line, ManualResetEventSlim? Flushed);

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
            // Invariant culture: in a custom format ':' is the culture's time separator, so without it
            // the log's timestamps would change shape with the user's regional settings.
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
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

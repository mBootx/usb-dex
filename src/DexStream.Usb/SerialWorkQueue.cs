using System.Collections.Concurrent;

namespace DexStream.Usb;

/// <summary>
/// Runs work items one at a time on a dedicated foreground-priority background thread.
/// </summary>
/// <remarks>
/// WinUSB is used here with synchronous, non-overlapped I/O: a blocking <c>WinUsb_ReadPipe</c> is
/// unblocked by <c>WinUsb_AbortPipe</c> from another thread. That is far less error-prone than
/// hand-rolled overlapped I/O, but it must not block a thread-pool thread, because an ADB read
/// legitimately blocks for as long as the device has nothing to say. A dedicated thread per pipe
/// keeps the pool free and keeps I/O ordering trivially correct.
/// </remarks>
internal sealed class SerialWorkQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private bool _disposed;

    public SerialWorkQueue(string name)
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
            // USB I/O feeds the video pipeline; a scheduling delay here shows up as a dropped frame.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Queues <paramref name="work"/> and completes when it has run.</summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return completion.Task;
        }

        try
        {
            _queue.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            completion.TrySetException(new ObjectDisposedException(nameof(SerialWorkQueue)));
        }

        return completion.Task;
    }

    private void Loop()
    {
        try
        {
            foreach (Action work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed while blocked on the queue.
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

        // The worker may be inside a blocking USB call; the caller aborts the pipe before disposing,
        // so a bounded join is enough and avoids hanging shutdown on a wedged driver.
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}

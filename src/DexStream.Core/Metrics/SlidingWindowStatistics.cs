namespace DexStream.Core.Metrics;

/// <summary>Summary of a sliding window of samples, in whatever unit was fed in.</summary>
public readonly record struct WindowStatistics(int Count, double Min, double Max, double Mean, double P50, double P95)
{
    public static WindowStatistics Empty => new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// A fixed-capacity ring buffer of doubles that reports min, max, mean and percentiles. Used for
/// latency and frame-interval figures in the metrics panel.
/// </summary>
/// <remarks>
/// Percentiles are computed by copying and sorting the window on demand. With the few hundred
/// samples the UI needs, that is far cheaper than maintaining an order statistic tree, and it keeps
/// <see cref="Add"/> on the hot path allocation free.
/// </remarks>
public sealed class SlidingWindowStatistics
{
    private readonly object _gate = new();
    private readonly double[] _samples;
    private int _next;
    private int _count;

    public SlidingWindowStatistics(int capacity = 240)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _samples = new double[capacity];
    }

    public int Capacity => _samples.Length;

    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        lock (_gate)
        {
            _samples[_next] = value;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }
        }
    }

    public WindowStatistics Snapshot()
    {
        double[] copy;
        int count;

        lock (_gate)
        {
            count = _count;
            if (count == 0)
            {
                return WindowStatistics.Empty;
            }

            copy = new double[count];
            Array.Copy(_samples, copy, count);
        }

        Array.Sort(copy);

        double sum = 0;
        foreach (double value in copy)
        {
            sum += value;
        }

        return new WindowStatistics(
            count,
            copy[0],
            copy[count - 1],
            sum / count,
            Percentile(copy, 0.50),
            Percentile(copy, 0.95));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>Nearest-rank percentile over an already sorted array.</summary>
    internal static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

namespace DexStream.Core.Metrics;

/// <summary>
/// Measures a per-second rate (frames or bytes) over a sliding time window.
/// </summary>
/// <remarks>
/// Samples are bucketed by 100 ms so the meter holds a bounded amount of state regardless of event
/// rate, which matters at 120 fps with one call per frame plus one per packet.
/// </remarks>
public sealed class RateMeter
{
    private const int BucketDurationMs = 100;

    private readonly object _gate = new();
    private readonly double[] _buckets;
    private readonly long[] _bucketStartMs;
    private readonly int _windowMs;

    public RateMeter(int windowMs = 1000)
    {
        if (windowMs < BucketDurationMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowMs), $"Window must be at least {BucketDurationMs} ms.");
        }

        _windowMs = windowMs;
        int bucketCount = (windowMs / BucketDurationMs) + 1;
        _buckets = new double[bucketCount];
        _bucketStartMs = new long[bucketCount];
        Array.Fill(_bucketStartMs, long.MinValue);
    }

    /// <summary>Records <paramref name="amount"/> at <paramref name="timestampMs"/>.</summary>
    public void Add(double amount, long timestampMs)
    {
        long bucketStart = timestampMs - (timestampMs % BucketDurationMs);
        int slot = (int)((bucketStart / BucketDurationMs) % _buckets.Length);

        lock (_gate)
        {
            if (_bucketStartMs[slot] != bucketStart)
            {
                _bucketStartMs[slot] = bucketStart;
                _buckets[slot] = 0;
            }

            _buckets[slot] += amount;
        }
    }

    /// <summary>The rate per second over the window ending at <paramref name="timestampMs"/>.</summary>
    public double RatePerSecond(long timestampMs)
    {
        long cutoff = timestampMs - _windowMs;
        double total = 0;

        lock (_gate)
        {
            for (int i = 0; i < _buckets.Length; i++)
            {
                if (_bucketStartMs[i] > cutoff && _bucketStartMs[i] <= timestampMs)
                {
                    total += _buckets[i];
                }
            }
        }

        return total * 1000.0 / _windowMs;
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_buckets);
            Array.Fill(_bucketStartMs, long.MinValue);
        }
    }
}

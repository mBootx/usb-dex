namespace DexStream.Core.Metrics;

using DexStream.Core.Protocol;

/// <summary>
/// Estimates the offset between the device's monotonic clock and the host's, so that a frame's
/// presentation timestamp can be compared against host time to get an end-to-end latency figure.
/// </summary>
/// <remarks>
/// <para>
/// Uses the same idea as NTP: for each round trip, assume the device handled the ping halfway
/// between send and receive. The sample with the smallest round-trip time is the one least distorted
/// by queuing, so that sample supplies the estimate rather than an average of all of them.
/// </para>
/// <para>
/// Instances are safe to feed from the control channel's reply loop while the UI reads
/// <see cref="OffsetUs"/>, because state is swapped in a single lock.
/// </para>
/// </remarks>
public sealed class ClockSynchronizer
{
    private readonly object _gate = new();
    private long _bestRoundTripUs = long.MaxValue;
    private long _offsetUs;
    private int _sampleCount;

    /// <summary>
    /// Discard the running best after this many samples so the estimate can follow clock drift and
    /// CPU frequency changes instead of being pinned by one lucky early round trip.
    /// </summary>
    public int ResetAfterSamples { get; init; } = 120;

    /// <summary>
    /// <c>deviceUs - hostUs</c> for the same instant. Subtract it from a device timestamp to get
    /// host time.
    /// </summary>
    public long OffsetUs
    {
        get
        {
            lock (_gate)
            {
                return _offsetUs;
            }
        }
    }

    /// <summary>Smallest round trip observed in the current window, in microseconds.</summary>
    public long BestRoundTripUs
    {
        get
        {
            lock (_gate)
            {
                return _bestRoundTripUs == long.MaxValue ? 0 : _bestRoundTripUs;
            }
        }
    }

    /// <summary>True once at least one round trip has completed.</summary>
    public bool HasEstimate
    {
        get
        {
            lock (_gate)
            {
                return _bestRoundTripUs != long.MaxValue;
            }
        }
    }

    /// <summary>Feeds one round trip into the estimate.</summary>
    public void Add(DexPong pong)
    {
        long roundTrip = pong.RoundTripUs;
        if (roundTrip < 0)
        {
            // A non-monotonic host clock; nothing useful can be derived from this sample.
            return;
        }

        lock (_gate)
        {
            if (++_sampleCount > ResetAfterSamples)
            {
                _sampleCount = 1;
                _bestRoundTripUs = long.MaxValue;
            }

            if (roundTrip >= _bestRoundTripUs)
            {
                return;
            }

            _bestRoundTripUs = roundTrip;
            long hostMidpoint = pong.HostSentUs + (roundTrip / 2);
            _offsetUs = pong.DeviceUs - hostMidpoint;
        }
    }

    /// <summary>
    /// Converts a device timestamp to host time. Returns the input unchanged until an estimate
    /// exists, which keeps early frames from reporting a nonsensical latency.
    /// </summary>
    public long ToHostTimeUs(long deviceTimestampUs)
    {
        lock (_gate)
        {
            return _bestRoundTripUs == long.MaxValue ? deviceTimestampUs : deviceTimestampUs - _offsetUs;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _bestRoundTripUs = long.MaxValue;
            _offsetUs = 0;
            _sampleCount = 0;
        }
    }
}

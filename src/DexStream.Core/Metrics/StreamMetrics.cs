using System.Diagnostics;

namespace DexStream.Core.Metrics;

/// <summary>Everything the metrics panel shows, captured at one instant.</summary>
public readonly record struct MetricsSnapshot
{
    public double FramesPerSecond { get; init; }

    public double MegabitsPerSecond { get; init; }

    /// <summary>Frames decoded and presented since the session started.</summary>
    public long FramesPresented { get; init; }

    /// <summary>Frames dropped because the renderer could not keep up.</summary>
    public long FramesDropped { get; init; }

    /// <summary>Key frames received, which also counts recoveries after packet loss.</summary>
    public long KeyFrames { get; init; }

    /// <summary>End-to-end latency from device capture to host present, in milliseconds.</summary>
    public WindowStatistics EndToEndLatencyMs { get; init; }

    /// <summary>Time from packet arrival to present, in milliseconds. Isolates host-side cost.</summary>
    public WindowStatistics DecodeLatencyMs { get; init; }

    /// <summary>Control-channel round trip, in milliseconds.</summary>
    public double ControlRoundTripMs { get; init; }

    /// <summary>Interval between presented frames, in milliseconds.</summary>
    public WindowStatistics FrameIntervalMs { get; init; }

    public static MetricsSnapshot Empty => new()
    {
        EndToEndLatencyMs = WindowStatistics.Empty,
        DecodeLatencyMs = WindowStatistics.Empty,
        FrameIntervalMs = WindowStatistics.Empty,
    };
}

/// <summary>
/// Collects streaming statistics. Counters are updated from the receive and render threads and read
/// by the UI on a timer, so every field is either interlocked or guarded inside its own helper.
/// </summary>
public sealed class StreamMetrics
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RateMeter _frameRate = new();
    private readonly RateMeter _bitRate = new();
    private readonly SlidingWindowStatistics _endToEndLatency = new();
    private readonly SlidingWindowStatistics _decodeLatency = new();
    private readonly SlidingWindowStatistics _frameInterval = new();

    private long _framesPresented;
    private long _framesDropped;
    private long _keyFrames;
    private long _lastPresentUs;
    private long _controlRoundTripUs;

    /// <summary>Host monotonic clock in microseconds, the time base every latency figure uses.</summary>
    public long NowUs => _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    /// <summary>Records an encoded packet as it arrives from the device.</summary>
    public void OnPacketReceived(int byteCount, bool isKeyFrame)
    {
        long nowMs = _clock.ElapsedMilliseconds;
        _bitRate.Add(byteCount * 8.0, nowMs);
        if (isKeyFrame)
        {
            Interlocked.Increment(ref _keyFrames);
        }
    }

    /// <summary>
    /// Records a presented frame.
    /// </summary>
    /// <param name="captureHostTimeUs">
    /// The frame's device capture timestamp translated into host time by
    /// <see cref="ClockSynchronizer"/>, or null when no clock estimate exists yet.
    /// </param>
    /// <param name="arrivalHostTimeUs">Host time when the encoded packet finished arriving.</param>
    public void OnFramePresented(long? captureHostTimeUs, long arrivalHostTimeUs)
    {
        long nowUs = NowUs;
        _frameRate.Add(1, nowUs / 1000);
        Interlocked.Increment(ref _framesPresented);

        if (captureHostTimeUs is { } capture)
        {
            long latency = nowUs - capture;
            // A negative figure means the clock estimate has not settled; feeding it in would
            // poison the percentiles, so drop the sample instead.
            if (latency >= 0)
            {
                _endToEndLatency.Add(latency / 1000.0);
            }
        }

        long decode = nowUs - arrivalHostTimeUs;
        if (decode >= 0)
        {
            _decodeLatency.Add(decode / 1000.0);
        }

        long previous = Interlocked.Exchange(ref _lastPresentUs, nowUs);
        if (previous != 0 && nowUs > previous)
        {
            _frameInterval.Add((nowUs - previous) / 1000.0);
        }
    }

    public void OnFrameDropped() => Interlocked.Increment(ref _framesDropped);

    public void OnControlRoundTrip(long roundTripUs) => Interlocked.Exchange(ref _controlRoundTripUs, roundTripUs);

    public MetricsSnapshot Snapshot()
    {
        long nowMs = _clock.ElapsedMilliseconds;
        return new MetricsSnapshot
        {
            FramesPerSecond = _frameRate.RatePerSecond(nowMs),
            MegabitsPerSecond = _bitRate.RatePerSecond(nowMs) / 1_000_000.0,
            FramesPresented = Interlocked.Read(ref _framesPresented),
            FramesDropped = Interlocked.Read(ref _framesDropped),
            KeyFrames = Interlocked.Read(ref _keyFrames),
            EndToEndLatencyMs = _endToEndLatency.Snapshot(),
            DecodeLatencyMs = _decodeLatency.Snapshot(),
            FrameIntervalMs = _frameInterval.Snapshot(),
            ControlRoundTripMs = Interlocked.Read(ref _controlRoundTripUs) / 1000.0,
        };
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _framesPresented, 0);
        Interlocked.Exchange(ref _framesDropped, 0);
        Interlocked.Exchange(ref _keyFrames, 0);
        Interlocked.Exchange(ref _lastPresentUs, 0);
        Interlocked.Exchange(ref _controlRoundTripUs, 0);
        _frameRate.Reset();
        _bitRate.Reset();
        _endToEndLatency.Reset();
        _decodeLatency.Reset();
        _frameInterval.Reset();
    }
}

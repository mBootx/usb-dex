using DexStream.Core.Metrics;
using DexStream.Core.Protocol;
using DexStream.Core.Session;
using Xunit;

namespace DexStream.Core.Tests;

public class SlidingWindowStatisticsTests
{
    [Fact]
    public void Snapshot_IsEmptyBeforeAnySamples()
        => Assert.Equal(WindowStatistics.Empty, new SlidingWindowStatistics(8).Snapshot());

    [Fact]
    public void Snapshot_ComputesMinMaxAndMean()
    {
        var stats = new SlidingWindowStatistics(8);
        foreach (double value in new[] { 4.0, 1.0, 3.0, 2.0 })
        {
            stats.Add(value);
        }

        WindowStatistics snapshot = stats.Snapshot();

        Assert.Equal(4, snapshot.Count);
        Assert.Equal(1.0, snapshot.Min);
        Assert.Equal(4.0, snapshot.Max);
        Assert.Equal(2.5, snapshot.Mean, precision: 6);
    }

    [Fact]
    public void Snapshot_OnlyKeepsTheMostRecentCapacitySamples()
    {
        var stats = new SlidingWindowStatistics(3);
        foreach (double value in new[] { 100.0, 1.0, 2.0, 3.0 })
        {
            stats.Add(value);
        }

        WindowStatistics snapshot = stats.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(3.0, snapshot.Max);  // the 100 has been overwritten
    }

    [Fact]
    public void Percentiles_UseNearestRank()
    {
        var stats = new SlidingWindowStatistics(100);
        for (int i = 1; i <= 100; i++)
        {
            stats.Add(i);
        }

        WindowStatistics snapshot = stats.Snapshot();

        Assert.Equal(50, snapshot.P50);
        Assert.Equal(95, snapshot.P95);
    }

    [Fact]
    public void Add_IgnoresNaNAndInfinity()
    {
        var stats = new SlidingWindowStatistics(4);

        stats.Add(double.NaN);
        stats.Add(double.PositiveInfinity);
        stats.Add(5);

        Assert.Equal(1, stats.Snapshot().Count);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCapacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowStatistics(0));

    [Fact]
    public void Percentile_HandlesASingleSample()
        => Assert.Equal(7.0, SlidingWindowStatistics.Percentile([7.0], 0.95));
}

public class RateMeterTests
{
    [Fact]
    public void RatePerSecond_IsZeroWithNoSamples()
        => Assert.Equal(0, new RateMeter().RatePerSecond(5000));

    [Fact]
    public void RatePerSecond_CountsEventsInsideTheWindow()
        => Assert.Equal(60, Measure(60, startMs: 1000, spacingMs: 16, atMs: 1960), precision: 0);

    [Fact]
    public void RatePerSecond_ForgetsSamplesOutsideTheWindow()
    {
        var meter = new RateMeter(windowMs: 1000);
        meter.Add(1000, 0);

        // Two seconds later the old bucket must have aged out.
        Assert.Equal(0, meter.RatePerSecond(2500));
    }

    [Fact]
    public void RatePerSecond_ScalesBytesToBitsPerSecond()
    {
        var meter = new RateMeter(windowMs: 1000);
        meter.Add(1_000_000, 100);
        meter.Add(1_000_000, 500);

        Assert.Equal(2_000_000, meter.RatePerSecond(900), precision: 0);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var meter = new RateMeter();
        meter.Add(100, 10);

        meter.Reset();

        Assert.Equal(0, meter.RatePerSecond(100));
    }

    [Fact]
    public void Constructor_RejectsAWindowShorterThanOneBucket()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RateMeter(50));

    private static double Measure(int count, long startMs, int spacingMs, long atMs)
    {
        var meter = new RateMeter(windowMs: 1000);
        for (int i = 0; i < count; i++)
        {
            meter.Add(1, startMs + (i * spacingMs));
        }

        return meter.RatePerSecond(atMs);
    }
}

public class ClockSynchronizerTests
{
    [Fact]
    public void HasEstimate_IsFalseUntilTheFirstSample()
    {
        var sync = new ClockSynchronizer();

        Assert.False(sync.HasEstimate);
        Assert.Equal(0, sync.OffsetUs);
        // Without an estimate the conversion is the identity, so latency reads as zero rather than
        // as a wild number.
        Assert.Equal(1234, sync.ToHostTimeUs(1234));
    }

    [Fact]
    public void Add_DerivesTheOffsetFromTheRoundTripMidpoint()
    {
        var sync = new ClockSynchronizer();

        // Sent at host 1000, received at host 2000, device clock read 1_000_500 in between.
        // Midpoint is host 1500, so the device clock is 999_000 ahead.
        sync.Add(new DexPong(1, HostSentUs: 1000, DeviceUs: 1_000_500, HostReceivedUs: 2000));

        Assert.True(sync.HasEstimate);
        Assert.Equal(999_000, sync.OffsetUs);
        Assert.Equal(1000, sync.BestRoundTripUs);
        Assert.Equal(1500, sync.ToHostTimeUs(1_000_500));
    }

    [Fact]
    public void Add_KeepsTheSampleWithTheSmallestRoundTrip()
    {
        var sync = new ClockSynchronizer();

        sync.Add(new DexPong(1, 0, 5_000_000, 10_000));     // 10 ms round trip
        sync.Add(new DexPong(2, 100_000, 5_100_100, 100_200)); // 0.2 ms round trip

        Assert.Equal(200, sync.BestRoundTripUs);
        Assert.Equal(5_100_100 - 100_100, sync.OffsetUs);
    }

    [Fact]
    public void Add_IgnoresALaterWorseSample()
    {
        var sync = new ClockSynchronizer();
        sync.Add(new DexPong(1, 0, 1_000, 200));
        long offset = sync.OffsetUs;

        sync.Add(new DexPong(2, 1_000_000, 9_999_999, 1_500_000));

        Assert.Equal(offset, sync.OffsetUs);
    }

    [Fact]
    public void Add_ForgetsTheBestSampleAfterTheResetWindowSoItCanTrackDrift()
    {
        var sync = new ClockSynchronizer { ResetAfterSamples = 3 };
        sync.Add(new DexPong(1, 0, 1_000, 100)); // very good sample

        for (int i = 0; i < 3; i++)
        {
            sync.Add(new DexPong(i + 2, 1_000_000, 2_000_000, 1_050_000)); // 50 ms round trips
        }

        Assert.Equal(50_000, sync.BestRoundTripUs);
    }

    [Fact]
    public void Add_IgnoresANegativeRoundTrip()
    {
        var sync = new ClockSynchronizer();

        sync.Add(new DexPong(1, HostSentUs: 5000, DeviceUs: 1, HostReceivedUs: 1000));

        Assert.False(sync.HasEstimate);
    }

    [Fact]
    public void Reset_ClearsTheEstimate()
    {
        var sync = new ClockSynchronizer();
        sync.Add(new DexPong(1, 0, 1000, 100));

        sync.Reset();

        Assert.False(sync.HasEstimate);
    }
}

public class StreamMetricsTests
{
    [Fact]
    public void Snapshot_StartsEmpty()
    {
        MetricsSnapshot snapshot = new StreamMetrics().Snapshot();

        Assert.Equal(0, snapshot.FramesPresented);
        Assert.Equal(0, snapshot.EndToEndLatencyMs.Count);
    }

    [Fact]
    public void Counters_Accumulate()
    {
        var metrics = new StreamMetrics();

        metrics.OnPacketReceived(1000, isKeyFrame: true);
        metrics.OnPacketReceived(500, isKeyFrame: false);
        metrics.OnFramePresented(captureHostTimeUs: null, arrivalHostTimeUs: metrics.NowUs);
        metrics.OnFramePresented(captureHostTimeUs: null, arrivalHostTimeUs: metrics.NowUs);
        metrics.OnFrameDropped();

        MetricsSnapshot snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.FramesPresented);
        Assert.Equal(1, snapshot.FramesDropped);
        Assert.Equal(1, snapshot.KeyFrames);
        Assert.True(snapshot.MegabitsPerSecond > 0);
    }

    [Fact]
    public void OnFramePresented_RecordsEndToEndLatencyWhenTheCaptureTimeIsKnown()
    {
        var metrics = new StreamMetrics();
        long capture = metrics.NowUs - 12_000; // 12 ms ago

        metrics.OnFramePresented(capture, arrivalHostTimeUs: metrics.NowUs);

        WindowStatistics latency = metrics.Snapshot().EndToEndLatencyMs;
        Assert.Equal(1, latency.Count);
        Assert.InRange(latency.Mean, 10, 60);
    }

    [Fact]
    public void OnFramePresented_DropsANegativeLatencySampleFromAnUnsettledClock()
    {
        var metrics = new StreamMetrics();

        metrics.OnFramePresented(captureHostTimeUs: metrics.NowUs + 10_000_000, arrivalHostTimeUs: 0);

        Assert.Equal(0, metrics.Snapshot().EndToEndLatencyMs.Count);
    }

    [Fact]
    public void OnControlRoundTrip_IsReportedInMilliseconds()
    {
        var metrics = new StreamMetrics();

        metrics.OnControlRoundTrip(4500);

        Assert.Equal(4.5, metrics.Snapshot().ControlRoundTripMs, precision: 3);
    }

    [Fact]
    public void Reset_ClearsCounters()
    {
        var metrics = new StreamMetrics();
        metrics.OnPacketReceived(100, true);
        metrics.OnFramePresented(null, metrics.NowUs);

        metrics.Reset();

        MetricsSnapshot snapshot = metrics.Snapshot();
        Assert.Equal(0, snapshot.FramesPresented);
        Assert.Equal(0, snapshot.KeyFrames);
    }
}

public class BitrateCalculatorTests
{
    [Theory]
    [InlineData(3840, 2160, 60, DexCodec.H265)]
    [InlineData(2560, 1440, 120, DexCodec.H264)]
    [InlineData(1920, 1080, 60, DexCodec.H264)]
    public void Recommend_StaysInsideTheClampRange(int width, int height, double fps, DexCodec codec)
    {
        int bitrate = BitrateCalculator.Recommend(width, height, fps, codec);

        Assert.InRange(bitrate, BitrateCalculator.MinBitrate, BitrateCalculator.MaxBitrate);
    }

    [Fact]
    public void Recommend_ScalesWithPixelRate()
    {
        int low = BitrateCalculator.Recommend(1920, 1080, 60, DexCodec.H264);
        int high = BitrateCalculator.Recommend(1920, 1080, 120, DexCodec.H264);

        Assert.True(high > low);
    }

    [Fact]
    public void Recommend_AsksForLessWithHevcThanWithH264()
    {
        int h264 = BitrateCalculator.Recommend(2560, 1440, 60, DexCodec.H264);
        int h265 = BitrateCalculator.Recommend(2560, 1440, 60, DexCodec.H265);

        Assert.True(h265 < h264);
    }

    [Fact]
    public void Recommend_FallsBackToTheMinimumForDegenerateInput()
        => Assert.Equal(BitrateCalculator.MinBitrate, BitrateCalculator.Recommend(0, 0, 60, DexCodec.H264));

    [Fact]
    public void Recommend_TreatsAnUnknownRefreshRateAs60Hz()
        => Assert.Equal(
            BitrateCalculator.Recommend(1920, 1080, 60, DexCodec.H264),
            BitrateCalculator.Recommend(1920, 1080, 0, DexCodec.H264));
}

public class StreamSessionOptionsTests
{
    [Fact]
    public void ResolveCodec_HonoursAnExplicitChoice()
    {
        Assert.Equal(
            DexCodec.H264,
            new StreamSessionOptions { Codec = CodecPreference.H264 }.ResolveCodec(3840, 2160));
        Assert.Equal(
            DexCodec.H265,
            new StreamSessionOptions { Codec = CodecPreference.H265 }.ResolveCodec(640, 480));
    }

    [Fact]
    public void ResolveCodec_PicksHevcAboveQuadHdAndH264BelowIt()
    {
        var options = new StreamSessionOptions { Codec = CodecPreference.Automatic };

        Assert.Equal(DexCodec.H264, options.ResolveCodec(2560, 1440));
        Assert.Equal(DexCodec.H265, options.ResolveCodec(3840, 2160));
    }
}

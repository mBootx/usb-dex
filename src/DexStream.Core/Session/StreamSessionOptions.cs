using DexStream.Core.Protocol;
using DexStream.Core.Rendering;

namespace DexStream.Core.Session;

/// <summary>Which video codec to ask the device encoder for.</summary>
public enum CodecPreference
{
    /// <summary>
    /// Pick per resolution: HEVC above 1440p because it halves the bitrate for the same quality,
    /// H.264 below it because its decoders have the shortest pipeline on Windows.
    /// </summary>
    Automatic,

    H264,
    H265,
}

/// <summary>Everything that configures one streaming session.</summary>
public sealed record StreamSessionOptions
{
    /// <summary>
    /// Longest edge to capture, in pixels. Zero means the display's native maximum. The agent scales
    /// on the device, which is cheaper than scaling after decode and reduces the encoded bitrate.
    /// </summary>
    public int MaxSize { get; init; }

    /// <summary>
    /// Requested refresh rate in Hz. Zero means the display's highest supported rate.
    /// </summary>
    public int TargetRefreshRate { get; init; }

    /// <summary>Encoder bitrate in bits per second. Zero asks for the automatic estimate.</summary>
    public int Bitrate { get; init; }

    public CodecPreference Codec { get; init; } = CodecPreference.Automatic;

    public ScalingMode Scaling { get; init; } = ScalingMode.Fit;

    /// <summary>Turn the phone's own panel off while streaming, which saves power and avoids touches.</summary>
    public bool TurnPhoneScreenOff { get; init; } = true;

    /// <summary>
    /// Refuse to stream the phone's own screen. When DeX is not running the session fails with an
    /// explanation instead of silently mirroring the handset.
    /// </summary>
    public bool RequireDexDisplay { get; init; }

    /// <summary>
    /// Try to start a desktop-mode display on the device when no DeX display is present. See
    /// <c>docs/DEX-ACTIVATION.md</c> for which strategies are known to work.
    /// </summary>
    public bool ActivateDexIfMissing { get; init; } = true;

    /// <summary>Explicit display id to capture. Null selects automatically.</summary>
    public int? DisplayId { get; init; }

    /// <summary>Interval between control-channel pings, which drive the clock and latency estimates.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Resolves <see cref="Codec"/> to a concrete codec for a given frame size.
    /// </summary>
    public DexCodec ResolveCodec(int width, int height) => Codec switch
    {
        CodecPreference.H264 => DexCodec.H264,
        CodecPreference.H265 => DexCodec.H265,
        _ => (long)width * height > 2560L * 1440 ? DexCodec.H265 : DexCodec.H264,
    };
}

/// <summary>
/// Picks an encoder bitrate from the frame size and refresh rate.
/// </summary>
/// <remarks>
/// Uses bits per pixel per frame rather than a fixed table, so an unusual DeX resolution gets a
/// sensible figure. The coefficients come from the rule of thumb for low-latency screen content,
/// which compresses far better than camera video: roughly 0.07 bpp for H.264 and 0.045 bpp for HEVC
/// at the same perceived quality. The result is clamped because USB 2.0 bulk throughput, not the
/// encoder, is the real ceiling on many cables.
/// </remarks>
public static class BitrateCalculator
{
    public const int MinBitrate = 2_000_000;

    /// <summary>
    /// Upper bound. USB 2.0 high speed tops out near 280 Mbit/s of usable bulk throughput and ADB
    /// framing takes a share of that, so going above this trades latency for no visible gain.
    /// </summary>
    public const int MaxBitrate = 100_000_000;

    public static int Recommend(int width, int height, double refreshRate, DexCodec codec)
    {
        if (width <= 0 || height <= 0)
        {
            return MinBitrate;
        }

        double fps = refreshRate > 1 ? refreshRate : 60;
        double bitsPerPixel = codec == DexCodec.H264 ? 0.07 : 0.045;
        double estimate = (double)width * height * fps * bitsPerPixel;

        return (int)Math.Clamp(estimate, MinBitrate, MaxBitrate);
    }
}

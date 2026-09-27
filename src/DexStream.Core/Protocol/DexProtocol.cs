namespace DexStream.Core.Protocol;

/// <summary>Video codec carried on the stream, encoded on the wire as a four-character code.</summary>
public enum DexCodec : uint
{
    Unknown = 0,

    /// <summary>H.264 / AVC, Annex B byte stream.</summary>
    H264 = 0x68_32_36_34, // "h264"

    /// <summary>H.265 / HEVC, Annex B byte stream.</summary>
    H265 = 0x68_32_36_35, // "h265"

    /// <summary>AV1, low-overhead bitstream.</summary>
    Av1 = 0x61_76_30_31, // "av01"
}

/// <summary>Packet kinds on the video channel (device to host).</summary>
public enum DexPacketType : byte
{
    /// <summary>Codec configuration: SPS/PPS for H.264, VPS/SPS/PPS for HEVC, sequence header for AV1.</summary>
    Config = 1,

    /// <summary>An encoded access unit.</summary>
    Frame = 2,

    /// <summary>The capture display changed size or rotated; the payload is a new stream header body.</summary>
    DisplayChanged = 3,

    /// <summary>Keep-alive sent when the display is idle so the host can distinguish idle from stalled.</summary>
    Heartbeat = 4,

    /// <summary>UTF-8 text copied on the device.</summary>
    Clipboard = 5,

    /// <summary>UTF-8 diagnostic text from the agent.</summary>
    Log = 6,
}

/// <summary>Per-packet flags.</summary>
[Flags]
public enum DexPacketFlags : byte
{
    None = 0,

    /// <summary>The access unit is an IDR / key frame and can be decoded standalone.</summary>
    KeyFrame = 1 << 0,

    /// <summary>The payload is codec configuration data rather than picture data.</summary>
    CodecConfig = 1 << 1,

    /// <summary>The encoder reached end of stream.</summary>
    EndOfStream = 1 << 2,
}

/// <summary>Control messages sent from the host to the device.</summary>
public enum DexControlType : byte
{
    /// <summary>A key down/up event.</summary>
    KeyEvent = 1,

    /// <summary>A block of text to inject, bypassing keycode mapping.</summary>
    Text = 2,

    /// <summary>A pointer (mouse or touch) event.</summary>
    PointerEvent = 3,

    /// <summary>A scroll wheel event.</summary>
    Scroll = 4,

    /// <summary>Ask the encoder for an immediate key frame.</summary>
    RequestKeyFrame = 5,

    /// <summary>Change the target bitrate in bits per second.</summary>
    SetBitrate = 6,

    /// <summary>Replace the device clipboard, optionally pasting afterwards.</summary>
    SetClipboard = 7,

    /// <summary>Turn the phone's own screen off while streaming, to save power.</summary>
    SetDisplayPower = 8,

    /// <summary>Round-trip probe used for clock sync and latency measurement.</summary>
    Ping = 9,

    /// <summary>Stop the agent and exit cleanly.</summary>
    Shutdown = 10,

    /// <summary>Back / Home / Recents style navigation shortcut.</summary>
    SystemAction = 11,
}

/// <summary>Replies sent from the device back over the control channel.</summary>
public enum DexControlReplyType : byte
{
    /// <summary>Answer to <see cref="DexControlType.Ping"/>.</summary>
    Pong = 1,

    /// <summary>The agent rejected a control message; the payload is UTF-8 text.</summary>
    Error = 2,
}

/// <summary>Pointer actions, matching the subset of Android's <c>MotionEvent</c> actions we inject.</summary>
public enum DexPointerAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
    Cancel = 3,
    HoverEnter = 4,
    HoverMove = 5,
    HoverExit = 6,
}

/// <summary>Button bitmask, matching Android's <c>MotionEvent.BUTTON_*</c> values.</summary>
[Flags]
public enum DexPointerButtons
{
    None = 0,
    Primary = 1 << 0,
    Secondary = 1 << 1,
    Tertiary = 1 << 2,
    Back = 1 << 3,
    Forward = 1 << 4,
}

/// <summary>Shared constants for the DexStream wire protocol.</summary>
public static class DexProtocol
{
    /// <summary>Magic at the start of the video channel: ASCII <c>DEXS</c>.</summary>
    public const uint VideoMagic = 0x44_45_58_53;

    /// <summary>Magic at the start of the control channel: ASCII <c>DEXC</c>.</summary>
    public const uint ControlMagic = 0x44_45_58_43;

    /// <summary>
    /// Protocol version. The host refuses an agent that does not match exactly, because the agent
    /// is shipped inside the host executable and the two are always upgraded together.
    /// </summary>
    public const ushort Version = 1;

    /// <summary>Size of the fixed video stream header in bytes.</summary>
    public const int StreamHeaderSize = 92;

    /// <summary>Bytes reserved for the device name inside the stream header.</summary>
    public const int DeviceNameSize = 64;

    /// <summary>Size of each video packet header in bytes.</summary>
    public const int PacketHeaderSize = 16;

    /// <summary>
    /// Largest packet the host will accept. A 4K key frame at a high bitrate stays far below this;
    /// anything larger means the stream is out of sync.
    /// </summary>
    public const int MaxPacketSize = 16 * 1024 * 1024;

    /// <summary>Abstract socket name the agent listens on. The host opens it via <c>localabstract:</c>.</summary>
    public const string SocketName = "dexstream";

    /// <summary>Name given to displays the agent creates, so they can be recognised in dumpsys.</summary>
    public const string AgentDisplayName = "DexStream Desktop";

    /// <summary>Where the agent jar is pushed on the device.</summary>
    public const string AgentRemotePath = "/data/local/tmp/dexstream-agent.jar";
}

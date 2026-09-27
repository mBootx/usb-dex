namespace DexStream.Core.Adb;

/// <summary>
/// ADB wire commands. The numeric values are the little-endian encoding of the
/// four ASCII characters used on the wire (for example <c>CNXN</c>), exactly as
/// defined by <c>system/core/adb/protocol.txt</c> in AOSP.
/// </summary>
public enum AdbCommand : uint
{
    Sync = 0x434E5953, // SYNC
    Connect = 0x4E584E43, // CNXN
    Auth = 0x48545541, // AUTH
    Open = 0x4E45504F, // OPEN
    Okay = 0x59414B4F, // OKAY
    Close = 0x45534C43, // CLSE
    Write = 0x45545257, // WRTE
    StartTls = 0x534C5453, // STLS
}

/// <summary>Sub-type carried in <c>arg0</c> of an <see cref="AdbCommand.Auth"/> message.</summary>
public enum AdbAuthType : uint
{
    /// <summary>Device -> host: a 20-byte random token that the host must sign.</summary>
    Token = 1,

    /// <summary>Host -> device: the token signed with the host's private key.</summary>
    Signature = 2,

    /// <summary>Host -> device: the host public key, prompting the on-device trust dialog.</summary>
    RsaPublicKey = 3,
}

public static class AdbProtocol
{
    /// <summary>Protocol version advertised by the host in the CNXN message.</summary>
    public const uint Version = 0x0100_0001;

    /// <summary>Oldest protocol version we accept from a device.</summary>
    public const uint MinimumVersion = 0x0100_0000;

    /// <summary>Size of the fixed message header in bytes.</summary>
    public const int HeaderSize = 24;

    /// <summary>
    /// Maximum payload advertised to the device. adbd never sends us more than the
    /// value it advertises in its own CNXN, so this bounds our receive buffers.
    /// </summary>
    public const int DefaultMaxPayload = 256 * 1024;

    /// <summary>Length of the authentication token sent by adbd.</summary>
    public const int AuthTokenLength = 20;

    /// <summary>
    /// Feature list advertised to the device. Keep this conservative: advertising a
    /// feature we do not implement (for example <c>shell_v2</c>) changes how adbd
    /// frames its replies and would break stream parsing.
    /// </summary>
    public const string HostFeatures = "cmd,stat_v2,ls_v2,fixed_push_mkdir,apex,abb,fixed_push_symlink_timestamp,abb_exec,remount_shell,track_app,sendrecv_v2,sendrecv_v2_brotli,sendrecv_v2_lz4,sendrecv_v2_zstd,sendrecv_v2_dry_run_send";
}

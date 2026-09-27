using System.Runtime.InteropServices;
using DexStream.Core.Protocol;
using Vortice.MediaFoundation;

namespace DexStream.Media;

/// <summary>A decoder transform found on this machine.</summary>
/// <param name="Name">The transform's friendly name, for example <c>Intel Hardware H.264 Decoder</c>.</param>
/// <param name="IsHardware">True when the transform is a hardware MFT.</param>
public readonly record struct VideoDecoderInfo(string Name, bool IsHardware)
{
    public override string ToString() => IsHardware ? $"{Name} (hardware)" : $"{Name} (software)";
}

/// <summary>
/// Enumerates the Media Foundation video decoders installed on this machine.
/// </summary>
/// <remarks>
/// Used by the diagnostics pane: "no hardware H.265 decoder" is the single most useful thing to know
/// when a 4K DeX stream will not play, and it is far easier to read off a list than to infer from a
/// failure code.
/// </remarks>
public static class VideoDecoderCatalog
{
    // MFT_ENUM_FLAG values from mfapi.h. Vortice's EnumFlag enum omits SORTANDFILTER, which is the
    // one that makes the platform return the preferred transform first, so the flags are composed
    // from the raw values.
    private const uint EnumFlagSyncMft = 0x0000_0001;
    private const uint EnumFlagAsyncMft = 0x0000_0002;
    private const uint EnumFlagHardware = 0x0000_0004;
    private const uint EnumFlagSortAndFilter = 0x0000_0040;

    /// <summary>Flags used when looking for a decoder to actually use.</summary>
    internal const uint PreferredEnumFlags =
        EnumFlagHardware | EnumFlagSyncMft | EnumFlagAsyncMft | EnumFlagSortAndFilter;

    /// <summary>Flags used when nothing hardware-accelerated could be found.</summary>
    internal const uint SoftwareEnumFlags = EnumFlagSyncMft | EnumFlagAsyncMft | EnumFlagSortAndFilter;

    /// <summary>The Media Foundation subtype GUID for a DexStream codec.</summary>
    public static Guid ToSubtype(DexCodec codec) => codec switch
    {
        DexCodec.H264 => VideoFormatGuids.H264,
        DexCodec.H265 => VideoFormatGuids.Hevc,
        DexCodec.Av1 => new Guid("31305641-0000-0010-8000-00AA00389B71"), // MFVideoFormat_AV1
        _ => throw new MediaPipelineException($"Codec {codec} has no Media Foundation subtype."),
    };

    /// <summary>Lists the decoders that can handle <paramref name="codec"/>, hardware ones first.</summary>
    public static IReadOnlyList<VideoDecoderInfo> Enumerate(DexCodec codec)
    {
        var results = new List<VideoDecoderInfo>();

        foreach ((uint flags, bool hardware) in new[] { (PreferredEnumFlags, true), (SoftwareEnumFlags, false) })
        {
            foreach (IMFActivate activate in EnumerateActivators(codec, flags))
            {
                try
                {
                    results.Add(new VideoDecoderInfo(ReadFriendlyName(activate), hardware));
                }
                finally
                {
                    activate.Dispose();
                }
            }

            if (results.Count > 0 && hardware)
            {
                // A hardware transform was found, so the software list would only add noise.
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Calls <c>MFTEnumEx</c> and marshals the returned array of <c>IMFActivate*</c>.
    /// </summary>
    /// <remarks>
    /// The native call hands back a CoTaskMem array of raw interface pointers. Each pointer already
    /// carries a reference, so wrapping it in a Vortice object and disposing that object is what
    /// releases it; the array itself has to be freed separately.
    /// </remarks>
    internal static List<IMFActivate> EnumerateActivators(DexCodec codec, uint flags)
    {
        var inputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = ToSubtype(codec),
        };

        var outputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12,
        };

        var activators = new List<IMFActivate>();
        IntPtr array = IntPtr.Zero;

        try
        {
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder,
                flags,
                inputType,
                outputType,
                out array,
                out uint count);

            if (array == IntPtr.Zero || count == 0)
            {
                return activators;
            }

            for (uint i = 0; i < count; i++)
            {
                IntPtr pointer = Marshal.ReadIntPtr(array, (int)(i * (uint)IntPtr.Size));
                if (pointer != IntPtr.Zero)
                {
                    activators.Add(new IMFActivate(pointer));
                }
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException)
        {
            // No transform matched. An empty list is the right answer, not an exception.
            return activators;
        }
        finally
        {
            if (array != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(array);
            }
        }

        return activators;
    }

    /// <summary>
    /// Reads <c>MFT_FRIENDLY_NAME_Attribute</c> from an activator.
    /// </summary>
    /// <remarks>
    /// The GUID is written out here because Vortice does not surface this particular key. It is only
    /// used for display text, so an unrecognised key degrades to "Unnamed decoder" rather than
    /// affecting behaviour.
    /// </remarks>
    private static string ReadFriendlyName(IMFActivate activate)
    {
        var friendlyName = new Guid("314FFACA-E71A-4a5b-B4F6-12EB5C44D2E7");

        try
        {
            return activate.GetString(friendlyName) is { Length: > 0 } name ? name : "Unnamed decoder";
        }
        catch (Exception)
        {
            return "Unnamed decoder";
        }
    }
}

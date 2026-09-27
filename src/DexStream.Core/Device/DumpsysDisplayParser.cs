using System.Globalization;
using System.Text.RegularExpressions;

namespace DexStream.Core.Device;

/// <summary>
/// Parses the output of <c>adb shell dumpsys display</c> into <see cref="DisplayInfo"/> records.
/// </summary>
/// <remarks>
/// <para>
/// <c>dumpsys</c> output is a debugging dump with no stability guarantee, and Samsung adds fields of
/// its own. The parser is therefore deliberately tolerant: every field is optional, unknown tokens
/// are ignored, and a display that yields at least a name and a resolution is kept. When the
/// <c>Logical Displays</c> section can be read it supplies the real display ids; otherwise ids fall
/// back to the order the devices were listed in, which matches Android's own numbering in practice.
/// </para>
/// </remarks>
public static partial class DumpsysDisplayParser
{
    [GeneratedRegex(
        """DisplayDeviceInfo\{"(?<name>[^"]*)":(?<body>.*?)\}(?=\s*(?:\r?\n|$))""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DisplayDeviceRegex();

    [GeneratedRegex("""uniqueId="(?<id>[^"]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex UniqueIdRegex();

    [GeneratedRegex(@"(?<!\w)(?<w>\d{2,5})\s*x\s*(?<h>\d{2,5})(?!\s*dpi)(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"\bmodeId\s+(?<id>-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveModeRegex();

    [GeneratedRegex(@"\bdensity\s+(?<dpi>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DensityRegex();

    [GeneratedRegex(@"\btype\s+(?<type>[A-Z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"(?<!committed)(?<!\w)state\s+(?<state>[A-Z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StateRegex();

    [GeneratedRegex(@"\bFLAG_[A-Z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex FlagRegex();

    [GeneratedRegex(
        @"\{id=(?<id>\d+),\s*width=(?<w>\d+),\s*height=(?<h>\d+),\s*fps=(?<fps>[\d.]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModeRegex();

    [GeneratedRegex(
        @"mDisplayId=(?<id>\d+)(?<body>.*?)(?=\n\s*(?:Display \d+:|mDisplayId=)|\z)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex LogicalDisplayRegex();

    [GeneratedRegex(@"mPrimaryDisplayDevice=(?<name>.*?)\s*(?:\r?\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PrimaryDeviceRegex();

    /// <summary>Parses a full <c>dumpsys display</c> dump.</summary>
    /// <returns>The displays found, ordered as <c>dumpsys</c> listed them. Never null.</returns>
    public static IReadOnlyList<DisplayInfo> Parse(string dumpsysOutput)
    {
        if (string.IsNullOrWhiteSpace(dumpsysOutput))
        {
            return [];
        }

        Dictionary<string, int> idsByDeviceName = ParseLogicalDisplayIds(dumpsysOutput);
        var results = new List<DisplayInfo>();
        var usedIds = new HashSet<int>();

        int index = 0;
        foreach (Match match in DisplayDeviceRegex().Matches(dumpsysOutput))
        {
            string name = match.Groups["name"].Value;
            string body = match.Groups["body"].Value;

            DisplayInfo? info = ParseDeviceInfo(name, body);
            if (info is null)
            {
                index++;
                continue;
            }

            int displayId = idsByDeviceName.TryGetValue(name, out int mapped) && usedIds.Add(mapped)
                ? mapped
                : index;

            results.Add(info with { DisplayId = displayId });
            index++;
        }

        return results;
    }

    /// <summary>
    /// Maps a display device name to the logical display id that uses it as its primary device.
    /// </summary>
    private static Dictionary<string, int> ParseLogicalDisplayIds(string dump)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match block in LogicalDisplayRegex().Matches(dump))
        {
            if (!int.TryParse(block.Groups["id"].Value, CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            Match device = PrimaryDeviceRegex().Match(block.Groups["body"].Value);
            if (!device.Success)
            {
                continue;
            }

            string name = device.Groups["name"].Value.Trim();
            if (name.Length > 0)
            {
                map.TryAdd(name, id);
            }
        }

        return map;
    }

    private static DisplayInfo? ParseDeviceInfo(string name, string body)
    {
        // supportedModes carries "width=... height=..." pairs that would otherwise be picked up by
        // the WxH probe, so read the modes first and then strip that section.
        List<DisplayMode> modes = [];
        foreach (Match mode in ModeRegex().Matches(body))
        {
            if (int.TryParse(mode.Groups["id"].Value, CultureInfo.InvariantCulture, out int modeId)
                && int.TryParse(mode.Groups["w"].Value, CultureInfo.InvariantCulture, out int modeW)
                && int.TryParse(mode.Groups["h"].Value, CultureInfo.InvariantCulture, out int modeH)
                && double.TryParse(mode.Groups["fps"].Value, CultureInfo.InvariantCulture, out double fps))
            {
                modes.Add(new DisplayMode(modeId, modeW, modeH, fps));
            }
        }

        string scalarBody = StripBracketedLists(body);

        Match resolution = ResolutionRegex().Match(scalarBody);
        if (!resolution.Success
            || !int.TryParse(resolution.Groups["w"].Value, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(resolution.Groups["h"].Value, CultureInfo.InvariantCulture, out int height))
        {
            return null;
        }

        int activeModeId = -1;
        Match activeMode = ActiveModeRegex().Match(scalarBody);
        if (activeMode.Success)
        {
            int.TryParse(activeMode.Groups["id"].Value, CultureInfo.InvariantCulture, out activeModeId);
        }

        int density = 0;
        Match densityMatch = DensityRegex().Match(scalarBody);
        if (densityMatch.Success)
        {
            int.TryParse(densityMatch.Groups["dpi"].Value, CultureInfo.InvariantCulture, out density);
        }

        double refreshRate = 0;
        foreach (DisplayMode mode in modes)
        {
            if (mode.Id == activeModeId)
            {
                refreshRate = mode.RefreshRate;
                break;
            }
        }

        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match flag in FlagRegex().Matches(body))
        {
            flags.Add(flag.Value);
        }

        Match typeMatch = TypeRegex().Match(scalarBody);
        Match stateMatch = StateRegex().Match(scalarBody);

        return new DisplayInfo
        {
            Name = name,
            UniqueId = UniqueIdRegex().Match(body) is { Success: true } uid ? uid.Groups["id"].Value : string.Empty,
            Kind = ParseKind(typeMatch.Success ? typeMatch.Groups["type"].Value : null),
            Width = width,
            Height = height,
            RefreshRate = refreshRate,
            DensityDpi = density,
            ActiveModeId = activeModeId,
            SupportedModes = modes,
            Flags = flags,
            State = stateMatch.Success ? stateMatch.Groups["state"].Value : string.Empty,
        };
    }

    /// <summary>
    /// Removes <c>[...]</c> and <c>{...}</c> groups so that nested structures such as
    /// <c>supportedModes</c>, <c>hdrCapabilities</c> and <c>deviceProductInfo</c> cannot be mistaken
    /// for the display's own scalar fields.
    /// </summary>
    internal static string StripBracketedLists(string body)
    {
        var sb = new System.Text.StringBuilder(body.Length);
        int depth = 0;
        foreach (char c in body)
        {
            if (c is '[' or '{')
            {
                depth++;
                sb.Append(' ');
                continue;
            }

            if (c is ']' or '}')
            {
                if (depth > 0)
                {
                    depth--;
                }

                sb.Append(' ');
                continue;
            }

            sb.Append(depth == 0 ? c : ' ');
        }

        return sb.ToString();
    }

    private static DisplayKind ParseKind(string? type) => type switch
    {
        "INTERNAL" or "BUILT_IN" => DisplayKind.Internal,
        "EXTERNAL" or "HDMI" => DisplayKind.External,
        "VIRTUAL" => DisplayKind.Virtual,
        "OVERLAY" => DisplayKind.Overlay,
        "WIFI" => DisplayKind.WifiDisplay,
        _ => DisplayKind.Unknown,
    };
}

namespace DexStream.Core.Device;

/// <summary>How Android classifies the panel behind a display.</summary>
public enum DisplayKind
{
    Unknown,
    Internal,
    External,
    Virtual,
    Overlay,
    WifiDisplay,
}

/// <summary>
/// A display as reported by <c>dumpsys display</c>, with everything DexStream needs to decide
/// whether it is the DeX desktop and at which mode to capture it.
/// </summary>
public sealed record DisplayInfo
{
    /// <summary>Logical display id, the value passed to the agent to select a capture source.</summary>
    public int DisplayId { get; init; } = -1;

    /// <summary>Display name, for example <c>Built-in Screen</c> or <c>Samsung DeX</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Android's stable identifier, for example <c>local:4619827259835644672</c>.</summary>
    public string UniqueId { get; init; } = string.Empty;

    public DisplayKind Kind { get; init; } = DisplayKind.Unknown;

    /// <summary>Width of the currently active mode, in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height of the currently active mode, in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Refresh rate of the currently active mode, in Hz. Zero when unknown.</summary>
    public double RefreshRate { get; init; }

    public int DensityDpi { get; init; }

    /// <summary>Mode id currently active, or -1 when <c>dumpsys</c> did not report one.</summary>
    public int ActiveModeId { get; init; } = -1;

    /// <summary>Every mode the display advertises, in the order <c>dumpsys</c> listed them.</summary>
    public IReadOnlyList<DisplayMode> SupportedModes { get; init; } = [];

    /// <summary><c>FLAG_*</c> tokens from the <c>DisplayDeviceInfo</c> line.</summary>
    public IReadOnlySet<string> Flags { get; init; } = new HashSet<string>();

    /// <summary>Power state string, for example <c>ON</c> or <c>OFF</c>.</summary>
    public string State { get; init; } = string.Empty;

    public bool IsDefaultDisplay => Flags.Contains("FLAG_DEFAULT_DISPLAY") || DisplayId == 0;

    /// <summary>
    /// A secure display refuses to be mirrored into an untrusted virtual display; capture must go
    /// through a trusted path or it will produce black frames.
    /// </summary>
    public bool IsSecure => Flags.Contains("FLAG_SECURE");

    public bool IsOn => State.Length == 0 || State.Equals("ON", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the name or unique id identifies this as a Samsung DeX desktop. Samsung labels the
    /// DeX display in both fields depending on One UI version and on whether DeX was started by a
    /// dock, by an HDMI cable, or as a virtual desktop.
    /// </summary>
    public bool LooksLikeDex =>
        Name.Contains("dex", StringComparison.OrdinalIgnoreCase)
        || UniqueId.Contains("dex", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The mode with the highest pixel count, breaking ties on refresh rate. This is what
    /// "maximum supported resolution and refresh rate" resolves to.
    /// </summary>
    public DisplayMode BestMode
    {
        get
        {
            if (SupportedModes.Count == 0)
            {
                return new DisplayMode(ActiveModeId, Width, Height, RefreshRate);
            }

            DisplayMode best = SupportedModes[0];
            foreach (DisplayMode mode in SupportedModes)
            {
                if (mode.PixelCount > best.PixelCount
                    || (mode.PixelCount == best.PixelCount && mode.RefreshRate > best.RefreshRate))
                {
                    best = mode;
                }
            }

            return best;
        }
    }

    /// <summary>The highest refresh rate available at the display's largest resolution.</summary>
    public double MaxRefreshRate
    {
        get
        {
            DisplayMode best = BestMode;
            double max = best.RefreshRate;
            foreach (DisplayMode mode in SupportedModes)
            {
                if (mode.PixelCount == best.PixelCount && mode.RefreshRate > max)
                {
                    max = mode.RefreshRate;
                }
            }

            return max;
        }
    }

    public override string ToString()
        => $"#{DisplayId} \"{Name}\" {Width}x{Height}@{RefreshRate:0.##}Hz ({Kind})";
}

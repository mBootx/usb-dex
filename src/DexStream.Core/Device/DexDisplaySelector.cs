namespace DexStream.Core.Device;

/// <summary>Why a particular display was chosen as the capture source.</summary>
public enum DexSelectionReason
{
    /// <summary>No display could be used.</summary>
    None,

    /// <summary>The display identifies itself as DeX by name or unique id.</summary>
    NamedDexDisplay,

    /// <summary>A secondary display created by the agent for desktop mode.</summary>
    AgentCreatedDisplay,

    /// <summary>A non-default external or virtual display, which is what DeX presents as.</summary>
    SecondaryDisplay,

    /// <summary>Only the phone's own screen was available.</summary>
    FallbackPrimaryDisplay,
}

/// <param name="Display">The chosen display, or null when the list was empty.</param>
/// <param name="Reason">Why it was chosen; drives the message shown in the UI.</param>
public readonly record struct DexDisplaySelection(DisplayInfo? Display, DexSelectionReason Reason)
{
    public bool IsDexDesktop => Reason is DexSelectionReason.NamedDexDisplay
        or DexSelectionReason.AgentCreatedDisplay;
}

/// <summary>
/// Chooses which display to capture.
/// </summary>
/// <remarks>
/// DeX presents as a second logical display on the phone. It is labelled inconsistently across One
/// UI versions and across how DeX was started (dock, USB-C to HDMI, DeX on a monitor, or the virtual
/// desktop the agent can create), so the selector ranks candidates rather than matching one name:
/// an explicit DeX label wins, then a display the agent created itself, then any non-default
/// external or virtual display, and only then the phone's own screen.
/// </remarks>
public static class DexDisplaySelector
{
    /// <summary>
    /// Picks the best capture source.
    /// </summary>
    /// <param name="displays">Displays reported by <see cref="DumpsysDisplayParser"/>.</param>
    /// <param name="agentDisplayName">
    /// Name the agent gives displays it creates, so they can be recognised unambiguously.
    /// </param>
    /// <param name="allowPrimaryFallback">
    /// When false, returns <see cref="DexSelectionReason.None"/> instead of mirroring the phone
    /// screen, so the UI can tell the user to start DeX first.
    /// </param>
    public static DexDisplaySelection Select(
        IReadOnlyList<DisplayInfo> displays,
        string? agentDisplayName = null,
        bool allowPrimaryFallback = true)
    {
        if (displays.Count == 0)
        {
            return new DexDisplaySelection(null, DexSelectionReason.None);
        }

        DisplayInfo? named = null;
        DisplayInfo? agentCreated = null;
        DisplayInfo? secondary = null;
        DisplayInfo? primary = null;

        foreach (DisplayInfo display in displays)
        {
            if (!display.IsOn)
            {
                continue;
            }

            if (agentDisplayName is { Length: > 0 }
                && display.Name.Equals(agentDisplayName, StringComparison.Ordinal))
            {
                agentCreated = Better(agentCreated, display);
                continue;
            }

            if (display.LooksLikeDex)
            {
                named = Better(named, display);
                continue;
            }

            if (!display.IsDefaultDisplay
                && display.Kind is DisplayKind.External or DisplayKind.Virtual or DisplayKind.Unknown)
            {
                secondary = Better(secondary, display);
                continue;
            }

            if (display.IsDefaultDisplay)
            {
                primary = Better(primary, display);
            }
        }

        if (named is not null)
        {
            return new DexDisplaySelection(named, DexSelectionReason.NamedDexDisplay);
        }

        if (agentCreated is not null)
        {
            return new DexDisplaySelection(agentCreated, DexSelectionReason.AgentCreatedDisplay);
        }

        if (secondary is not null)
        {
            return new DexDisplaySelection(secondary, DexSelectionReason.SecondaryDisplay);
        }

        if (primary is not null && allowPrimaryFallback)
        {
            return new DexDisplaySelection(primary, DexSelectionReason.FallbackPrimaryDisplay);
        }

        return new DexDisplaySelection(null, DexSelectionReason.None);
    }

    /// <summary>Prefers the larger display, breaking ties on refresh rate.</summary>
    private static DisplayInfo Better(DisplayInfo? current, DisplayInfo candidate)
    {
        if (current is null)
        {
            return candidate;
        }

        long currentPixels = (long)current.Width * current.Height;
        long candidatePixels = (long)candidate.Width * candidate.Height;

        if (candidatePixels > currentPixels)
        {
            return candidate;
        }

        if (candidatePixels == currentPixels && candidate.RefreshRate > current.RefreshRate)
        {
            return candidate;
        }

        return current;
    }

    /// <summary>A short explanation of the selection, suitable for the status line in the UI.</summary>
    public static string Describe(DexDisplaySelection selection) => selection.Reason switch
    {
        DexSelectionReason.NamedDexDisplay =>
            $"Streaming the Samsung DeX desktop ({selection.Display}).",
        DexSelectionReason.AgentCreatedDisplay =>
            $"Streaming a DexStream desktop display created on the phone ({selection.Display}).",
        DexSelectionReason.SecondaryDisplay =>
            $"Streaming a secondary display ({selection.Display}). If this is not the DeX desktop, " +
            "start DeX on the phone and reconnect.",
        DexSelectionReason.FallbackPrimaryDisplay =>
            $"DeX is not running, so the phone screen is being mirrored ({selection.Display}).",
        _ => "No usable display was found on the device.",
    };
}

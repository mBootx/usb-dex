using DexStream.Core.Device;
using DexStream.Core.Protocol;
using Xunit;

namespace DexStream.Core.Tests;

public class DexDisplaySelectorTests
{
    [Fact]
    public void Select_PrefersTheNamedDexDisplay()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex);

        DexDisplaySelection selection = DexDisplaySelector.Select(displays);

        Assert.Equal(DexSelectionReason.NamedDexDisplay, selection.Reason);
        Assert.Equal(2, selection.Display!.DisplayId);
        Assert.True(selection.IsDexDesktop);
    }

    [Fact]
    public void Select_PrefersTheAgentDisplayWhenItIsNotLabelledDex()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.AgentCreatedDisplay);

        DexDisplaySelection selection = DexDisplaySelector.Select(displays, DexProtocol.AgentDisplayName);

        Assert.Equal(DexSelectionReason.AgentCreatedDisplay, selection.Reason);
        Assert.Equal(5, selection.Display!.DisplayId);
    }

    [Fact]
    public void Select_FallsBackToThePhoneScreenWhenDexIsNotRunning()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneOnly);

        DexDisplaySelection selection = DexDisplaySelector.Select(displays);

        Assert.Equal(DexSelectionReason.FallbackPrimaryDisplay, selection.Reason);
        Assert.Equal(0, selection.Display!.DisplayId);
        Assert.False(selection.IsDexDesktop);
    }

    [Fact]
    public void Select_ReturnsNoneWhenThePrimaryFallbackIsNotAllowed()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneOnly);

        DexDisplaySelection selection = DexDisplaySelector.Select(displays, allowPrimaryFallback: false);

        Assert.Equal(DexSelectionReason.None, selection.Reason);
        Assert.Null(selection.Display);
    }

    [Fact]
    public void Select_TreatsAnUnlabelledSecondaryDisplayAsACandidate()
    {
        DisplayInfo[] displays =
        [
            new() { DisplayId = 0, Name = "Built-in Screen", Kind = DisplayKind.Internal, Width = 1440, Height = 3120, State = "ON", Flags = new HashSet<string> { "FLAG_DEFAULT_DISPLAY" } },
            new() { DisplayId = 3, Name = "HDMI", Kind = DisplayKind.External, Width = 1920, Height = 1080, State = "ON", Flags = new HashSet<string>() },
        ];

        DexDisplaySelection selection = DexDisplaySelector.Select(displays);

        Assert.Equal(DexSelectionReason.SecondaryDisplay, selection.Reason);
        Assert.Equal(3, selection.Display!.DisplayId);
    }

    [Fact]
    public void Select_SkipsDisplaysThatAreOff()
    {
        DisplayInfo[] displays =
        [
            new() { DisplayId = 2, Name = "Samsung DeX", Kind = DisplayKind.Virtual, Width = 3840, Height = 2160, State = "OFF", Flags = new HashSet<string>() },
            new() { DisplayId = 0, Name = "Built-in Screen", Kind = DisplayKind.Internal, Width = 1440, Height = 3120, State = "ON", Flags = new HashSet<string> { "FLAG_DEFAULT_DISPLAY" } },
        ];

        DexDisplaySelection selection = DexDisplaySelector.Select(displays);

        Assert.Equal(DexSelectionReason.FallbackPrimaryDisplay, selection.Reason);
    }

    [Fact]
    public void Select_PrefersTheLargerOfTwoDexCandidates()
    {
        DisplayInfo[] displays =
        [
            new() { DisplayId = 2, Name = "DeX small", Kind = DisplayKind.Virtual, Width = 1920, Height = 1080, RefreshRate = 60, State = "ON", Flags = new HashSet<string>() },
            new() { DisplayId = 3, Name = "DeX large", Kind = DisplayKind.Virtual, Width = 3840, Height = 2160, RefreshRate = 60, State = "ON", Flags = new HashSet<string>() },
        ];

        DexDisplaySelection selection = DexDisplaySelector.Select(displays);

        Assert.Equal(3, selection.Display!.DisplayId);
    }

    [Fact]
    public void Select_BreaksTiesOnRefreshRate()
    {
        DisplayInfo[] displays =
        [
            new() { DisplayId = 2, Name = "DeX 60", Kind = DisplayKind.Virtual, Width = 2560, Height = 1440, RefreshRate = 60, State = "ON", Flags = new HashSet<string>() },
            new() { DisplayId = 3, Name = "DeX 120", Kind = DisplayKind.Virtual, Width = 2560, Height = 1440, RefreshRate = 120, State = "ON", Flags = new HashSet<string>() },
        ];

        Assert.Equal(3, DexDisplaySelector.Select(displays).Display!.DisplayId);
    }

    [Fact]
    public void Select_HandlesAnEmptyList()
    {
        DexDisplaySelection selection = DexDisplaySelector.Select([]);

        Assert.Equal(DexSelectionReason.None, selection.Reason);
        Assert.Contains("No usable display", DexDisplaySelector.Describe(selection));
    }

    [Fact]
    public void Describe_MentionsTheChosenDisplay()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex);

        string description = DexDisplaySelector.Describe(DexDisplaySelector.Select(displays));

        Assert.Contains("Samsung DeX", description);
        Assert.Contains("3840x2160", description);
    }
}

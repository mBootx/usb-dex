using DexStream.Core.Device;
using Xunit;

namespace DexStream.Core.Tests;

public class DumpsysDisplayParserTests
{
    [Fact]
    public void Parse_FindsBothDisplays()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex);

        Assert.Equal(2, displays.Count);
        Assert.Equal("Built-in Screen", displays[0].Name);
        Assert.Equal("Samsung DeX", displays[1].Name);
    }

    [Fact]
    public void Parse_ReadsTheLogicalDisplayIdRatherThanTheListIndex()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex);

        Assert.Equal(0, displays[0].DisplayId);
        // The DeX display is second in the list but is logical display 2.
        Assert.Equal(2, displays[1].DisplayId);
    }

    [Fact]
    public void Parse_ReadsTheActiveResolutionNotTheDensityOrTheModeList()
    {
        DisplayInfo phone = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex)[0];

        Assert.Equal(1440, phone.Width);
        Assert.Equal(3120, phone.Height);
        Assert.Equal(600, phone.DensityDpi);
    }

    [Fact]
    public void Parse_ResolvesTheActiveRefreshRateFromTheModeList()
    {
        DisplayInfo phone = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex)[0];

        Assert.Equal(2, phone.ActiveModeId);
        Assert.Equal(120.0, phone.RefreshRate, precision: 1);
    }

    [Fact]
    public void Parse_ReadsEverySupportedMode()
    {
        DisplayInfo phone = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex)[0];

        Assert.Equal(3, phone.SupportedModes.Count);
        Assert.Contains(phone.SupportedModes, m => m is { Id: 1, Width: 1440, Height: 3120 });
        Assert.Contains(phone.SupportedModes, m => m is { Id: 3, Width: 1080, Height: 2340 });
    }

    [Fact]
    public void Parse_ReadsFlagsAndKindAndState()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex);

        Assert.Equal(DisplayKind.Internal, displays[0].Kind);
        Assert.True(displays[0].IsDefaultDisplay);
        Assert.True(displays[0].IsSecure);
        Assert.True(displays[0].IsOn);

        Assert.Equal(DisplayKind.Virtual, displays[1].Kind);
        Assert.False(displays[1].IsDefaultDisplay);
        Assert.Contains("FLAG_PRESENTATION", displays[1].Flags);
    }

    [Fact]
    public void Parse_ReadsTheUniqueId()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex);

        Assert.Equal("local:4619827259835644672", displays[0].UniqueId);
        Assert.StartsWith("virtual:com.samsung.android.desktopmode", displays[1].UniqueId);
    }

    [Fact]
    public void Parse_DetectsTheDexDisplay()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex);

        Assert.False(displays[0].LooksLikeDex);
        Assert.True(displays[1].LooksLikeDex);
    }

    [Fact]
    public void Parse_ReadsAPoweredOffDisplayAsOff()
    {
        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(DumpsysSamples.AgentCreatedDisplay);

        Assert.False(displays[0].IsOn);
        Assert.True(displays[1].IsOn);
    }

    [Fact]
    public void BestMode_PicksTheLargestThenTheFastest()
    {
        DisplayInfo phone = DumpsysDisplayParser.Parse(DumpsysSamples.PhoneWithDex)[0];

        DisplayMode best = phone.BestMode;

        Assert.Equal(1440, best.Width);
        Assert.Equal(3120, best.Height);
        Assert.Equal(120.0, best.RefreshRate, precision: 1);
        Assert.Equal(120.0, phone.MaxRefreshRate, precision: 1);
    }

    [Fact]
    public void BestMode_FallsBackToTheActiveModeWhenNoModesAreListed()
    {
        var display = new DisplayInfo { Width = 800, Height = 600, RefreshRate = 75, ActiveModeId = 9 };

        Assert.Equal(new DisplayMode(9, 800, 600, 75), display.BestMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some unrelated dumpsys output")]
    public void Parse_ReturnsEmptyForUnusableInput(string input)
        => Assert.Empty(DumpsysDisplayParser.Parse(input));

    [Fact]
    public void Parse_IgnoresTrailingGarbageAfterTheDisplaySection()
    {
        string dump = DumpsysSamples.PhoneOnly + "\n\nSomething else entirely\n  mFoo=1\n";

        Assert.Single(DumpsysDisplayParser.Parse(dump));
    }

    [Fact]
    public void StripBracketedLists_RemovesNestedStructures()
    {
        string stripped = DumpsysDisplayParser.StripBracketedLists(
            "a=1, modes [{width=99, height=88}], b=2, info Info{x=3}, c=4");

        Assert.Contains("a=1", stripped);
        Assert.Contains("b=2", stripped);
        Assert.Contains("c=4", stripped);
        Assert.DoesNotContain("99", stripped);
        Assert.DoesNotContain("x=3", stripped);
    }
}

using DexStream.Core.Rendering;
using Xunit;

namespace DexStream.Core.Tests;

public class ViewportTests
{
    [Fact]
    public void Fit_LetterboxesAWideSourceInATallWindow()
    {
        Viewport viewport = Viewport.Compute(1600, 1000, 1920, 1080);

        Assert.Equal(1600, viewport.Width);
        Assert.Equal(900, viewport.Height); // 1600 * 1080/1920
        Assert.Equal(0, viewport.X);
        Assert.Equal(50, viewport.Y);       // (1000 - 900) / 2
    }

    [Fact]
    public void Fit_PillarboxesATallSourceInAWideWindow()
    {
        Viewport viewport = Viewport.Compute(1920, 1080, 1440, 3120);

        Assert.Equal(1080, viewport.Height);
        Assert.Equal(498, viewport.Width); // round(1080 * 1440/3120)
        Assert.Equal(0, viewport.Y);
        Assert.True(viewport.X > 0);
    }

    [Fact]
    public void Fit_MatchesExactlyWhenTheAspectRatiosAgree()
    {
        Viewport viewport = Viewport.Compute(2560, 1440, 3840, 2160);

        Assert.Equal(new Viewport(0, 0, 2560, 1440, 3840, 2160), viewport);
    }

    [Fact]
    public void Stretch_FillsTheWindow()
    {
        Viewport viewport = Viewport.Compute(800, 600, 1920, 1080, ScalingMode.Stretch);

        Assert.Equal(new Viewport(0, 0, 800, 600, 1920, 1080), viewport);
    }

    [Fact]
    public void IntegerFit_SnapsToAWholeMultipleWhenUpscaling()
    {
        // 2.5x would fit; integer fit must drop to 2x.
        Viewport viewport = Viewport.Compute(1600, 1200, 640, 480, ScalingMode.IntegerFit);

        Assert.Equal(1280, viewport.Width);
        Assert.Equal(960, viewport.Height);
    }

    [Fact]
    public void IntegerFit_StillDownscalesFreelyWhenTheWindowIsSmaller()
    {
        Viewport viewport = Viewport.Compute(640, 480, 1920, 1080, ScalingMode.IntegerFit);

        Assert.Equal(640, viewport.Width);
        Assert.True(viewport.Height is > 0 and < 480);
    }

    [Theory]
    [InlineData(0, 100, 10, 10)]
    [InlineData(100, 0, 10, 10)]
    [InlineData(100, 100, 0, 10)]
    [InlineData(100, 100, 10, 0)]
    public void Compute_ReturnsEmptyForDegenerateInput(int cw, int ch, int sw, int sh)
    {
        Viewport viewport = Viewport.Compute(cw, ch, sw, sh);

        Assert.True(viewport.IsEmpty);
        Assert.Equal(new DevicePoint(0, 0), viewport.MapToDevice(5, 5));
    }

    [Fact]
    public void Compute_NeverCollapsesToZeroSize()
    {
        Viewport viewport = Viewport.Compute(1, 1, 3840, 2160);

        Assert.True(viewport.Width >= 1);
        Assert.True(viewport.Height >= 1);
        Assert.False(viewport.IsEmpty);
    }

    [Fact]
    public void MapToDevice_MapsTheCornersAndCentre()
    {
        Viewport viewport = Viewport.Compute(1920, 1080, 3840, 2160);

        Assert.Equal(new DevicePoint(0, 0), viewport.MapToDevice(0, 0));
        Assert.Equal(new DevicePoint(1920, 1080), viewport.MapToDevice(960, 540));
        Assert.Equal(new DevicePoint(3839, 2159), viewport.MapToDevice(1919.9, 1079.9));
    }

    [Fact]
    public void MapToDevice_AccountsForTheLetterboxOffset()
    {
        // 1600x1000 window, 1920x1080 source: the image is 1600x900 with a 50px band top and bottom.
        Viewport viewport = Viewport.Compute(1600, 1000, 1920, 1080);

        // A click at the very top of the window is above the image; it clamps to row 0.
        Assert.Equal(0, viewport.MapToDevice(800, 0).Y);
        // A click at the vertical centre of the window is the vertical centre of the image.
        Assert.Equal(540, viewport.MapToDevice(800, 500).Y);
    }

    [Fact]
    public void MapToDevice_ClampsPointsOutsideTheImage()
    {
        Viewport viewport = Viewport.Compute(1920, 1080, 3840, 2160);

        Assert.Equal(new DevicePoint(0, 0), viewport.MapToDevice(-500, -500));
        Assert.Equal(new DevicePoint(3839, 2159), viewport.MapToDevice(9999, 9999));
    }

    [Fact]
    public void Contains_DistinguishesTheImageFromTheLetterbox()
    {
        Viewport viewport = Viewport.Compute(1600, 1000, 1920, 1080);

        Assert.False(viewport.Contains(800, 0));    // top letterbox band
        Assert.True(viewport.Contains(800, 500));   // on the image
        Assert.False(viewport.Contains(800, 999));  // bottom letterbox band
    }

    [Fact]
    public void Scale_ReportsTheRatioUsedForRendering()
    {
        Viewport viewport = Viewport.Compute(1920, 1080, 3840, 2160);

        Assert.Equal(0.5, viewport.ScaleX, precision: 6);
        Assert.Equal(0.5, viewport.ScaleY, precision: 6);
    }
}

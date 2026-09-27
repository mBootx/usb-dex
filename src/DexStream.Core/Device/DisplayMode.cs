namespace DexStream.Core.Device;

/// <summary>One entry from a display's <c>supportedModes</c> list.</summary>
/// <param name="Id">Android's mode id, used with <c>SurfaceControl</c> to select a mode.</param>
/// <param name="Width">Mode width in pixels.</param>
/// <param name="Height">Mode height in pixels.</param>
/// <param name="RefreshRate">Refresh rate in Hz.</param>
public readonly record struct DisplayMode(int Id, int Width, int Height, double RefreshRate)
{
    public long PixelCount => (long)Width * Height;

    /// <summary>Pixels per second this mode delivers; the figure that actually bounds the encoder.</summary>
    public double PixelRate => PixelCount * RefreshRate;

    public override string ToString()
        => $"{Width}x{Height}@{RefreshRate:0.##}Hz";
}

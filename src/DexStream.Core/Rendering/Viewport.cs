namespace DexStream.Core.Rendering;

/// <summary>How the device image is fitted into the window.</summary>
public enum ScalingMode
{
    /// <summary>Preserve aspect ratio, letterboxing as needed. The default.</summary>
    Fit,

    /// <summary>Fill the window, ignoring aspect ratio.</summary>
    Stretch,

    /// <summary>
    /// Preserve aspect ratio and clamp to whole-pixel multiples, which avoids resampling blur when
    /// the window happens to be an exact multiple of the source.
    /// </summary>
    IntegerFit,
}

/// <summary>A point in device display pixels.</summary>
public readonly record struct DevicePoint(int X, int Y);

/// <summary>
/// The rectangle the decoded image occupies inside the window, and the mapping between window and
/// device coordinates that mouse input needs.
/// </summary>
/// <remarks>
/// Kept free of any UI framework type so the geometry can be unit tested without a display.
/// All values are in physical pixels; the caller applies DPI scaling before constructing one.
/// </remarks>
public readonly record struct Viewport(int X, int Y, int Width, int Height, int SourceWidth, int SourceHeight)
{
    public static Viewport Empty => new(0, 0, 0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0 || SourceWidth <= 0 || SourceHeight <= 0;

    /// <summary>Scale factor from source pixels to window pixels along X.</summary>
    public double ScaleX => SourceWidth <= 0 ? 1 : (double)Width / SourceWidth;

    /// <summary>Scale factor from source pixels to window pixels along Y.</summary>
    public double ScaleY => SourceHeight <= 0 ? 1 : (double)Height / SourceHeight;

    /// <summary>
    /// Computes the destination rectangle for a <paramref name="sourceWidth"/> x
    /// <paramref name="sourceHeight"/> image inside a <paramref name="clientWidth"/> x
    /// <paramref name="clientHeight"/> window.
    /// </summary>
    public static Viewport Compute(
        int clientWidth,
        int clientHeight,
        int sourceWidth,
        int sourceHeight,
        ScalingMode mode = ScalingMode.Fit)
    {
        if (clientWidth <= 0 || clientHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return Empty;
        }

        if (mode == ScalingMode.Stretch)
        {
            return new Viewport(0, 0, clientWidth, clientHeight, sourceWidth, sourceHeight);
        }

        double scale = Math.Min((double)clientWidth / sourceWidth, (double)clientHeight / sourceHeight);

        if (mode == ScalingMode.IntegerFit && scale >= 1.0)
        {
            scale = Math.Floor(scale);
        }

        // Never collapse to nothing: a window smaller than the source still shows a 1px sliver
        // rather than an empty viewport, which would make the whole surface untargetable.
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        return new Viewport(
            (clientWidth - width) / 2,
            (clientHeight - height) / 2,
            width,
            height,
            sourceWidth,
            sourceHeight);
    }

    /// <summary>True when the window point falls on the image rather than the letterbox border.</summary>
    public bool Contains(double clientX, double clientY)
        => !IsEmpty
            && clientX >= X && clientX < X + Width
            && clientY >= Y && clientY < Y + Height;

    /// <summary>
    /// Maps a window point to device display pixels, clamping to the display bounds so that a drag
    /// which leaves the window still produces a valid coordinate rather than an out-of-range one.
    /// </summary>
    public DevicePoint MapToDevice(double clientX, double clientY)
    {
        if (IsEmpty)
        {
            return new DevicePoint(0, 0);
        }

        double relativeX = (clientX - X) / Width;
        double relativeY = (clientY - Y) / Height;

        int deviceX = (int)Math.Round(relativeX * SourceWidth);
        int deviceY = (int)Math.Round(relativeY * SourceHeight);

        return new DevicePoint(
            Math.Clamp(deviceX, 0, SourceWidth - 1),
            Math.Clamp(deviceY, 0, SourceHeight - 1));
    }
}

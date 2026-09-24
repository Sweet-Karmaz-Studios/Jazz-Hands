namespace JazzHands.Core.Commands;

/// <summary>A colour read from the picture.</summary>
/// <param name="Red">Linear light, BT.709, straight (not premultiplied).</param>
/// <param name="Green">Linear light.</param>
/// <param name="Blue">Linear light.</param>
/// <param name="Hex">The same colour as sRGB hex, the way a colour parameter is written.</param>
/// <param name="X">Where it was read, in frame pixels from the left.</param>
/// <param name="Y">In frame pixels from the top.</param>
public sealed record ColorSample(double Red, double Green, double Blue, string Hex, int X, int Y);

/// <summary>What the scopes read on one frame.</summary>
/// <param name="Width">The frame measured.</param>
/// <param name="Height">Its height.</param>
/// <param name="SampledPixels">How many pixels were counted; a quarter of them on frames over a megapixel.</param>
/// <param name="Red">Pixels at each of 256 code values of red.</param>
/// <param name="Green">Green.</param>
/// <param name="Blue">Blue.</param>
/// <param name="Luma">BT.709 luma.</param>
/// <param name="ClippedHigh">The share of pixels at code value 255 luma, 0 to 1.</param>
/// <param name="ClippedLow">The share at 0.</param>
/// <param name="LumaLow">The luma code value below which the darkest 1% lie.</param>
/// <param name="LumaHigh">The one above which the brightest 1% lie.</param>
/// <param name="LumaMean">The average luma code value.</param>
public sealed record ScopeSummary(
    int Width,
    int Height,
    long SampledPixels,
    int[] Red,
    int[] Green,
    int[] Blue,
    int[] Luma,
    double ClippedHigh,
    double ClippedLow,
    int LumaLow,
    int LumaHigh,
    double LumaMean);

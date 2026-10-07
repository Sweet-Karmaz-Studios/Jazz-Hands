namespace JazzHands.App.Controls.Timeline;

/// <summary>How a waveform's samples become heights on a clip.</summary>
public static class WaveformScale
{
    /// <summary>The level that sits on the middle line on the decibel scale.</summary>
    public const double FloorDecibels = -60.0;

    /// <summary>
    /// A sample, -1 to 1, as a height on a decibel scale: 0 dB at the edge, <see cref="FloorDecibels"/>
    /// and quieter on the middle line, linear in decibels between, keeping its sign. Quiet speech that
    /// is a hair on the linear scale shows a third of the way out.
    /// </summary>
    public static float Decibels(float sample)
    {
        double level = Math.Abs(sample);
        if (level <= 0)
        {
            return 0;
        }

        double height = Math.Clamp((20 * Math.Log10(level) - FloorDecibels) / -FloorDecibels, 0, 1);
        return (float)(Math.Sign(sample) * height);
    }
}

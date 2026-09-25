using System.Numerics;

namespace JazzHands.Render.Effects.Impact;

/// <summary>How a zoom punch comes back from its peak.</summary>
public enum Settle
{
    /// <summary>Eases back to rest.</summary>
    Smooth,

    /// <summary>Goes a little past rest, then back.</summary>
    Back,

    /// <summary>Wobbles to rest, like a spring.</summary>
    Elastic,
}

/// <summary>
/// The impact effects' curves over time, worked out on the CPU in doubles from the time and a
/// seed alone, so frame n is the same whether it is played to or rendered cold.
/// </summary>
public static class ImpactCurves
{
    /// <summary>
    /// A shake's offset in pixels and its turn in degrees at a time: smooth seeded noise at a
    /// frequency, full strength from the trigger and dying away at the decay rate (per second; 0
    /// never dies). Nothing before the trigger.
    /// </summary>
    public static (Vector2 Offset, float Degrees) Shake(double time, double trigger, double frequency, double amplitude, double rotation, double decay, uint seed)
    {
        if (time < trigger)
        {
            return (Vector2.Zero, 0);
        }

        double since = time - trigger;
        double envelope = decay > 0 ? Math.Exp(-decay * since) : 1.0;
        double at = since * frequency;
        return (
            new Vector2((float)(Noise(at, seed, 0) * amplitude * envelope), (float)(Noise(at, seed, 1) * amplitude * envelope)),
            (float)(Noise(at, seed, 2) * rotation * envelope));
    }

    /// <summary>
    /// A zoom punch's scale at a time: 1 until the trigger, up to the amount over the attack (fast
    /// at first), then back to 1 over the settle by the chosen curve.
    /// </summary>
    public static double Punch(double time, double trigger, double amount, double attack, double settle, Settle curve)
    {
        double since = time - trigger;
        if (since < 0)
        {
            return 1.0;
        }

        double peak = amount - 1.0;
        if (since < attack)
        {
            double u = since / attack;
            return 1.0 + (peak * (1.0 - Math.Pow(1.0 - u, 3)));
        }

        double v = settle > 0 ? (since - attack) / settle : 1.0;
        if (v >= 1.0)
        {
            return 1.0;
        }

        double left = curve switch
        {
            Settle.Back => BackOut(v),
            Settle.Elastic => ElasticOut(v),
            _ => SmoothOut(v),
        };
        return 1.0 + (peak * left);
    }

    /// <summary>
    /// A flash's strength from 0 to 1 at a time: up over the attack, held, then fading over the
    /// decay, fast at first as a camera flash does.
    /// </summary>
    public static double Flash(double time, double trigger, double attack, double hold, double decay)
    {
        double since = time - trigger;
        if (since < 0)
        {
            return 0.0;
        }

        if (since < attack)
        {
            return since / attack;
        }

        since -= attack;
        if (since <= hold)
        {
            return 1.0;
        }

        since -= hold;
        if (decay <= 0 || since >= decay)
        {
            return 0.0;
        }

        double left = 1.0 - (since / decay);
        return left * left;
    }

    /// <summary>
    /// Smooth one dimensional gradient noise from -1 to 1, a different stream for each seed and
    /// channel, whole numbers of <paramref name="x"/> being where it passes through zero.
    /// </summary>
    public static double Noise(double x, uint seed, uint channel) => Core.Drivers.DriverNoise.Noise(x, seed, channel);

    private static double SmoothOut(double v) => 1.0 - (v * v * (3 - (2 * v)));

    private static double BackOut(double v)
    {
        // Past rest by about a tenth of the peak two thirds of the way, then back.
        const double C = 1.70158;
        double u = v - 1;
        double eased = 1 + ((C + 1) * u * u * u) + (C * u * u);
        return 1.0 - eased;
    }

    private static double ElasticOut(double v) =>
        Math.Pow(2, -8 * v) * Math.Cos(v * Math.PI * 4.5) * (1 - v);
}

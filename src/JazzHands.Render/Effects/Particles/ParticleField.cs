using System.Numerics;

namespace JazzHands.Render.Effects.Particles;

/// <summary>Where the particles come from.</summary>
public enum EmitterShape
{
    /// <summary>One point.</summary>
    Point,

    /// <summary>A horizontal line as wide as the emitter.</summary>
    Line,

    /// <summary>Anywhere inside a rectangle.</summary>
    Rectangle,
}

/// <summary>Everything that decides a particle field, in sequence pixels and seconds.</summary>
/// <param name="Seed">What makes one field different from another with the same settings.</param>
/// <param name="Shape">Where particles start.</param>
/// <param name="Position">The emitter's centre, from the frame centre.</param>
/// <param name="Size">The emitter's width and height.</param>
/// <param name="Rate">Particles a second.</param>
/// <param name="Lifetime">Seconds a particle lives.</param>
/// <param name="LifetimeVariance">How much lifetimes differ, 0 to 1.</param>
/// <param name="Speed">Pixels a second at birth.</param>
/// <param name="SpeedVariance">How much speeds differ, 0 to 1.</param>
/// <param name="Direction">Degrees clockwise from the right: -90 is up, 90 down.</param>
/// <param name="Spread">Degrees either side of the direction together: 360 is every way.</param>
/// <param name="Gravity">Pixels a second a second.</param>
/// <param name="Turbulence">How far particles wander from their path, in pixels.</param>
/// <param name="TurbulenceFrequency">How often the wander turns, a second.</param>
/// <param name="SizeStart">Diameter at birth.</param>
/// <param name="SizeEnd">Diameter at death.</param>
/// <param name="ColorStart">Premultiplied linear colour at birth.</param>
/// <param name="ColorEnd">Premultiplied linear colour at death.</param>
/// <param name="Prewarm">Start with the field already full, as if it had been running a lifetime.</param>
public sealed record ParticleSettings(
    int Seed,
    EmitterShape Shape,
    Vector2 Position,
    Vector2 Size,
    float Rate,
    float Lifetime,
    float LifetimeVariance,
    float Speed,
    float SpeedVariance,
    float Direction,
    float Spread,
    Vector2 Gravity,
    float Turbulence,
    float TurbulenceFrequency,
    float SizeStart,
    float SizeEnd,
    Vector4 ColorStart,
    Vector4 ColorEnd,
    bool Prewarm);

/// <summary>One particle at a moment.</summary>
/// <param name="Position">Where it is, from the frame centre.</param>
/// <param name="Velocity">Which way and how fast it is going, for a streak.</param>
/// <param name="Diameter">How big it is.</param>
/// <param name="Color">Premultiplied linear colour, faded in at birth and out at death.</param>
public readonly record struct Particle(Vector2 Position, Vector2 Velocity, float Diameter, Vector4 Color);

/// <summary>
/// A particle field as a function of time: every particle's birth, path and look worked out from
/// the seed and its number, never stepped from the frame before.
/// </summary>
/// <remarks>
/// A field that is simulated needs every earlier frame, or checkpoints, to reach frame 500, and a
/// scrub backwards replays it. Here particle n is born at a time fixed by n and the rate, moves by
/// its birth velocity, gravity and a smooth noise wander that are all closed form in its age, and
/// dies at its lifetime; its random choices are hashes of the seed and n. So any frame is the same
/// whether it is drawn cold, reached by playing or drawn twice, and an export is bit exact.
/// </remarks>
public static class ParticleField
{
    /// <summary>The most particles one moment draws, whatever the rate and lifetime ask.</summary>
    public const int MaxParticles = 20000;

    /// <summary>The particles alive at a moment, from the clip's start, in seconds.</summary>
    public static List<Particle> At(ParticleSettings settings, double seconds)
    {
        var alive = new List<Particle>();
        At(settings, seconds, alive);
        return alive;
    }

    /// <summary>The particles alive at a moment into a list, emptied first, so a field drawn every frame allocates nothing once the list has grown.</summary>
    public static void At(ParticleSettings settings, double seconds, List<Particle> alive)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(alive);
        alive.Clear();
        if (settings.Rate <= 0 || settings.Lifetime <= 0)
        {
            return;
        }

        double longest = settings.Lifetime * (1 + Math.Clamp(settings.LifetimeVariance, 0, 1));
        double start = settings.Prewarm ? -longest : 0;
        long first = Math.Max(0, (long)Math.Floor((seconds - longest - start) * settings.Rate) - 1);
        long last = (long)Math.Floor((seconds - start) * settings.Rate) + 1;
        first = Math.Max(first, last - MaxParticles);

        for (long number = first; number <= last; number++)
        {
            double birth = start + ((number + Hash(settings.Seed, number, 0)) / settings.Rate);
            double age = seconds - birth;
            float life = settings.Lifetime * (1 + (Math.Clamp(settings.LifetimeVariance, 0, 1) * Signed(settings.Seed, number, 1)));
            if (age < 0 || age >= life || life <= 0)
            {
                continue;
            }

            alive.Add(Make(settings, number, (float)age, life));
        }
    }

    /// <summary>A number from 0 to 1 that depends only on the seed, the particle and which choice it is.</summary>
    public static float Hash(int seed, long number, int choice)
    {
        ulong value = unchecked(((ulong)(uint)seed * 0x9E3779B97F4A7C15UL) ^ ((ulong)number * 0xBF58476D1CE4E5B9UL) ^ ((ulong)(uint)choice * 0x94D049BB133111EBUL));
        value ^= value >> 30;
        value = unchecked(value * 0xBF58476D1CE4E5B9UL);
        value ^= value >> 27;
        value = unchecked(value * 0x94D049BB133111EBUL);
        value ^= value >> 31;
        return (float)((value >> 40) / (double)(1UL << 24));
    }

    private static float Signed(int seed, long number, int choice) => (Hash(seed, number, choice) * 2) - 1;

    private static Particle Make(ParticleSettings settings, long number, float age, float life)
    {
        int seed = settings.Seed;
        Vector2 origin = settings.Position + settings.Shape switch
        {
            EmitterShape.Line => new Vector2(Signed(seed, number, 2) * settings.Size.X / 2, 0),
            EmitterShape.Rectangle => new Vector2(Signed(seed, number, 2) * settings.Size.X / 2, Signed(seed, number, 3) * settings.Size.Y / 2),
            _ => Vector2.Zero,
        };

        float angle = (settings.Direction + (Signed(seed, number, 4) * settings.Spread / 2)) * MathF.PI / 180;
        float speed = settings.Speed * (1 + (Math.Clamp(settings.SpeedVariance, 0, 1) * Signed(seed, number, 5)));
        var velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;

        Vector2 wander = settings.Turbulence > 0
            ? new Vector2(Noise(seed, number, 6, age * settings.TurbulenceFrequency), Noise(seed, number, 7, age * settings.TurbulenceFrequency)) * settings.Turbulence
            : Vector2.Zero;

        Vector2 position = origin + (velocity * age) + (0.5f * settings.Gravity * age * age) + wander;
        float progress = age / life;
        float diameter = float.Lerp(settings.SizeStart, settings.SizeEnd, progress);

        // In over the first tenth of its life, out over the last third.
        float fade = Math.Clamp(progress / 0.1f, 0, 1) * Math.Clamp((1 - progress) / 0.3f, 0, 1);
        Vector4 color = Vector4.Lerp(settings.ColorStart, settings.ColorEnd, progress) * fade;

        return new Particle(position, velocity + (settings.Gravity * age), diameter, color);
    }

    /// <summary>Smooth noise from -1 to 1 along one axis, the same for the same particle and choice.</summary>
    private static float Noise(int seed, long number, int choice, float x)
    {
        float floor = MathF.Floor(x);
        float fraction = x - floor;
        float smooth = fraction * fraction * (3 - (2 * fraction));
        long step = (long)floor;
        float a = Hash(seed, (number * 7919) + step, choice);
        float b = Hash(seed, (number * 7919) + step + 1, choice);
        return (float.Lerp(a, b, smooth) * 2) - 1;
    }
}

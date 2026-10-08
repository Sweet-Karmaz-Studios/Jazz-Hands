using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Particles;

/// <summary>
/// A particle field drawn on the GPU: embers, sparks, dust, snow, rain or sparkles, each a
/// generator with its own defaults and the same parameters.
/// </summary>
/// <remarks>
/// The field is <see cref="ParticleField"/>, a function of the clip's time and seed, so a frame
/// is the same however it is reached. Dots are antialiased discs, streaks round capped lines
/// along the particle's velocity; additive fields add light, and each dot gets a faint halo.
/// Sizes and positions are sequence pixels, scaled to the working resolution. Every particle is
/// one instance of one draw (Particles.hlsl): each used to be a Direct2D ellipse, about a hundred
/// times slower for a dense field at 4K.
/// </remarks>
public abstract class ParticleGenerator : VideoGenerator, ITimedGenerator
{
    private static readonly PassDescriptor Pass = new("Particles.hlsl", "PsParticle", "VsParticle", Vertices: 4);

    // Scratch, reused frame to frame; one generator draws every clip of its type on one thread.
    private readonly List<Particle> _field = [];
    private ParticleDot[] _dots = new ParticleDot[1024];

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes => [Pass];

    /// <summary>The settings a clip's parameters make, with its seed.</summary>
    public static ParticleSettings Settings(ParameterSet parameters, int seed)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new ParticleSettings(
            unchecked(seed + (parameters.Int("seed") * 7919)),
            parameters.Enum("emitter") switch
            {
                "line" => EmitterShape.Line,
                "rectangle" => EmitterShape.Rectangle,
                _ => EmitterShape.Point,
            },
            parameters.Float2("position"),
            parameters.Float2("emitter-size"),
            parameters.Float("rate"),
            parameters.Float("lifetime"),
            parameters.Float("lifetime-variance"),
            parameters.Float("speed"),
            parameters.Float("speed-variance"),
            parameters.Float("direction"),
            parameters.Float("spread"),
            parameters.Float2("gravity"),
            parameters.Float("turbulence"),
            parameters.Float("turbulence-frequency"),
            parameters.Float("size-start"),
            parameters.Float("size-end"),
            parameters.Color("color-start"),
            parameters.Color("color-end"),
            parameters.Bool("prewarm"));
    }

    /// <inheritdoc />
    public override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        ParticleField.At(Settings(parameters, context.Seed), context.Time.ToSeconds(), _field);
        float scale = context.QualityScale;
        var centre = new Vector2(output.Width, output.Height) / 2.0f;
        bool additive = parameters.Enum("blend") != "normal";
        float streak = parameters.Float("streak");

        // A halo and a dot each for additive dots, one shape for the rest.
        int most = _field.Count * (additive && streak <= 0 ? 2 : 1);
        if (_dots.Length < most)
        {
            _dots = new ParticleDot[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)most)];
        }

        int count = 0;
        foreach (Particle particle in _field)
        {
            Vector4 colour = particle.Color;
            if (colour.W <= 0.001f)
            {
                continue;
            }

            Vector2 at = centre + (particle.Position * scale);
            float radius = Math.Max(0.25f, particle.Diameter * scale / 2);
            if (streak > 0)
            {
                _dots[count++] = ParticleDot.Of(at - (particle.Velocity * streak * scale), at, radius, colour);
                continue;
            }

            if (additive)
            {
                _dots[count++] = ParticleDot.Of(at, at, radius * 2.5f, colour * 0.2f);
            }

            _dots[count++] = ParticleDot.Of(at, at, radius, colour);
        }

        context.Clear(output, Vector4.Zero);
        context.DrawInstances(Pass, output, Vector4.Zero, _dots.AsSpan(0, count), additive ? InstanceBlend.Add : InstanceBlend.Over);
    }
}

/// <summary>One dot or streak as Particles.hlsl reads it: a segment in target texels, a radius, and a premultiplied colour.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ParticleDot
{
    public Vector2 From;
    public Vector2 To;
    public float Radius;
    public float Weight;
    public Vector2 Padding;
    public Vector4 Color;

    /// <summary>
    /// A shape from one point to another. One under half a texel across is drawn half a texel
    /// across and fainter by the area it lacks, as Direct2D's coverage did, rather than vanishing
    /// or coming out as bright as a texel.
    /// </summary>
    public static ParticleDot Of(Vector2 from, Vector2 to, float radius, Vector4 color)
    {
        float drawn = Math.Max(radius, 0.5f);
        float ratio = radius / drawn;
        return new ParticleDot { From = from, To = to, Radius = drawn, Weight = ratio * ratio, Color = color };
    }
}

/// <summary>Glowing embers drifting up from below.</summary>
[Generator("gen.particles.embers", Name = "Embers", Category = "Particles", Description = "Glowing embers rising and drifting from the bottom of the frame, adding light. A fire, a forge, a burning title.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different field with the same settings.")]
[Param("emitter", ParamType.Enum, Default = "rectangle", Choices = "point, line, rectangle", Animatable = false, Description = "Where particles start: one point, a line as wide as the emitter, or anywhere in it.")]
[Param("position", ParamType.Point, Default = "0, 520", Unit = "px", Description = "The emitter's centre, in sequence pixels from the frame centre.")]
[Param("emitter-size", ParamType.Float2, Default = "1800, 60", Unit = "px", Description = "The emitter's width and height.")]
[Param("rate", ParamType.Float, Default = "60", Min = 0, Max = 5000, SliderMax = 500, Description = "Particles a second.")]
[Param("lifetime", ParamType.Float, Default = "3.5", Min = 0.01, Max = 60, SliderMax = 10, Unit = "s", Description = "How long each lives.")]
[Param("lifetime-variance", ParamType.Float, Default = "0.4", Min = 0, Max = 1, Description = "How much lifetimes differ.")]
[Param("speed", ParamType.Float, Default = "120", Min = 0, Max = 10000, SliderMax = 1500, Unit = "px", Description = "Pixels a second at birth.")]
[Param("speed-variance", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How much speeds differ.")]
[Param("direction", ParamType.Float, Default = "-90", Min = -360, Max = 360, Unit = "deg", Description = "Degrees clockwise from the right: -90 is up, 90 down.")]
[Param("spread", ParamType.Float, Default = "30", Min = 0, Max = 360, Unit = "deg", Description = "The fan of directions, in degrees: 360 is every way.")]
[Param("gravity", ParamType.Float2, Default = "0, -20", Unit = "px", Description = "Pixels a second a second; negative y pulls up.")]
[Param("turbulence", ParamType.Float, Default = "40", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "How far particles wander from their path.")]
[Param("turbulence-frequency", ParamType.Float, Label = "Turbulence rate", Default = "0.6", Min = 0, Max = 20, SliderMax = 5, Description = "How often the wander turns, a second.")]
[Param("size-start", ParamType.Float, Default = "6", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at birth.")]
[Param("size-end", ParamType.Float, Default = "2", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at death.")]
[Param("color-start", ParamType.Color, Default = "#FFA040", Description = "Colour at birth.")]
[Param("color-end", ParamType.Color, Default = "#FF3000", Description = "Colour at death.")]
[Param("blend", ParamType.Enum, Default = "additive", Choices = "additive, normal", Animatable = false, Description = "Add light, for fire and magic, or lay over, for snow and dust.")]
[Param("streak", ParamType.Float, Default = "0", Min = 0, Max = 1, SliderMax = 0.1, Unit = "s", Description = "Draw each particle as a line along its path this many seconds long; 0 for dots.")]
[Param("prewarm", ParamType.Bool, Default = "true", Animatable = false, Description = "Start already full, as if the field had been running.")]
public sealed class EmbersGenerator : ParticleGenerator;

/// <summary>A shower of sparks from a point, falling under gravity.</summary>
[Generator("gen.particles.sparks", Name = "Sparks", Category = "Particles", Description = "A burst of hot sparks from a point that arc and fall, streaking. An impact, a weld, a sword clash.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different field with the same settings.")]
[Param("emitter", ParamType.Enum, Default = "point", Choices = "point, line, rectangle", Animatable = false, Description = "Where particles start: one point, a line as wide as the emitter, or anywhere in it.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The emitter's centre, in sequence pixels from the frame centre.")]
[Param("emitter-size", ParamType.Float2, Default = "20, 20", Unit = "px", Description = "The emitter's width and height.")]
[Param("rate", ParamType.Float, Default = "150", Min = 0, Max = 5000, SliderMax = 500, Description = "Particles a second.")]
[Param("lifetime", ParamType.Float, Default = "0.8", Min = 0.01, Max = 60, SliderMax = 10, Unit = "s", Description = "How long each lives.")]
[Param("lifetime-variance", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How much lifetimes differ.")]
[Param("speed", ParamType.Float, Default = "700", Min = 0, Max = 10000, SliderMax = 1500, Unit = "px", Description = "Pixels a second at birth.")]
[Param("speed-variance", ParamType.Float, Default = "0.6", Min = 0, Max = 1, Description = "How much speeds differ.")]
[Param("direction", ParamType.Float, Default = "-90", Min = -360, Max = 360, Unit = "deg", Description = "Degrees clockwise from the right: -90 is up, 90 down.")]
[Param("spread", ParamType.Float, Default = "120", Min = 0, Max = 360, Unit = "deg", Description = "The fan of directions, in degrees: 360 is every way.")]
[Param("gravity", ParamType.Float2, Default = "0, 900", Unit = "px", Description = "Pixels a second a second; positive y pulls down.")]
[Param("turbulence", ParamType.Float, Default = "0", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "How far particles wander from their path.")]
[Param("turbulence-frequency", ParamType.Float, Label = "Turbulence rate", Default = "1", Min = 0, Max = 20, SliderMax = 5, Description = "How often the wander turns, a second.")]
[Param("size-start", ParamType.Float, Default = "3", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at birth.")]
[Param("size-end", ParamType.Float, Default = "1", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at death.")]
[Param("color-start", ParamType.Color, Default = "#FFE9A0", Description = "Colour at birth.")]
[Param("color-end", ParamType.Color, Default = "#FF6A00", Description = "Colour at death.")]
[Param("blend", ParamType.Enum, Default = "additive", Choices = "additive, normal", Animatable = false, Description = "Add light, for fire and magic, or lay over, for snow and dust.")]
[Param("streak", ParamType.Float, Default = "0.03", Min = 0, Max = 1, SliderMax = 0.1, Unit = "s", Description = "Draw each particle as a line along its path this many seconds long; 0 for dots.")]
[Param("prewarm", ParamType.Bool, Default = "false", Animatable = false, Description = "Start already full, as if the field had been running.")]
public sealed class SparksGenerator : ParticleGenerator;

/// <summary>Dust motes hanging in the air.</summary>
[Generator("gen.particles.dust", Name = "Dust motes", Category = "Particles", Description = "Soft dust hanging and drifting slowly all over the frame, catching the light. An old room, a sunbeam.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different field with the same settings.")]
[Param("emitter", ParamType.Enum, Default = "rectangle", Choices = "point, line, rectangle", Animatable = false, Description = "Where particles start: one point, a line as wide as the emitter, or anywhere in it.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The emitter's centre, in sequence pixels from the frame centre.")]
[Param("emitter-size", ParamType.Float2, Default = "1920, 1080", Unit = "px", Description = "The emitter's width and height.")]
[Param("rate", ParamType.Float, Default = "20", Min = 0, Max = 5000, SliderMax = 500, Description = "Particles a second.")]
[Param("lifetime", ParamType.Float, Default = "8", Min = 0.01, Max = 60, SliderMax = 10, Unit = "s", Description = "How long each lives.")]
[Param("lifetime-variance", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How much lifetimes differ.")]
[Param("speed", ParamType.Float, Default = "15", Min = 0, Max = 10000, SliderMax = 1500, Unit = "px", Description = "Pixels a second at birth.")]
[Param("speed-variance", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How much speeds differ.")]
[Param("direction", ParamType.Float, Default = "0", Min = -360, Max = 360, Unit = "deg", Description = "Degrees clockwise from the right: -90 is up, 90 down.")]
[Param("spread", ParamType.Float, Default = "360", Min = 0, Max = 360, Unit = "deg", Description = "The fan of directions, in degrees: 360 is every way.")]
[Param("gravity", ParamType.Float2, Default = "0, 0", Unit = "px", Description = "Pixels a second a second.")]
[Param("turbulence", ParamType.Float, Default = "25", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "How far particles wander from their path.")]
[Param("turbulence-frequency", ParamType.Float, Label = "Turbulence rate", Default = "0.2", Min = 0, Max = 20, SliderMax = 5, Description = "How often the wander turns, a second.")]
[Param("size-start", ParamType.Float, Default = "3", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at birth.")]
[Param("size-end", ParamType.Float, Default = "3", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at death.")]
[Param("color-start", ParamType.Color, Default = "#FFF4E080", Description = "Colour at birth.")]
[Param("color-end", ParamType.Color, Default = "#FFF4E080", Description = "Colour at death.")]
[Param("blend", ParamType.Enum, Default = "normal", Choices = "additive, normal", Animatable = false, Description = "Add light, for fire and magic, or lay over, for snow and dust.")]
[Param("streak", ParamType.Float, Default = "0", Min = 0, Max = 1, SliderMax = 0.1, Unit = "s", Description = "Draw each particle as a line along its path this many seconds long; 0 for dots.")]
[Param("prewarm", ParamType.Bool, Default = "true", Animatable = false, Description = "Start already full, as if the field had been running.")]
public sealed class DustGenerator : ParticleGenerator;

/// <summary>Snow falling from the top of the frame.</summary>
[Generator("gen.particles.snow", Name = "Snow", Category = "Particles", Description = "Snowflakes falling and swaying from the top of the frame. A winter level, a quiet end card.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different field with the same settings.")]
[Param("emitter", ParamType.Enum, Default = "rectangle", Choices = "point, line, rectangle", Animatable = false, Description = "Where particles start: one point, a line as wide as the emitter, or anywhere in it.")]
[Param("position", ParamType.Point, Default = "0, -600", Unit = "px", Description = "The emitter's centre, in sequence pixels from the frame centre.")]
[Param("emitter-size", ParamType.Float2, Default = "2100, 20", Unit = "px", Description = "The emitter's width and height.")]
[Param("rate", ParamType.Float, Default = "80", Min = 0, Max = 5000, SliderMax = 500, Description = "Particles a second.")]
[Param("lifetime", ParamType.Float, Default = "9", Min = 0.01, Max = 60, SliderMax = 10, Unit = "s", Description = "How long each lives.")]
[Param("lifetime-variance", ParamType.Float, Default = "0.3", Min = 0, Max = 1, Description = "How much lifetimes differ.")]
[Param("speed", ParamType.Float, Default = "60", Min = 0, Max = 10000, SliderMax = 1500, Unit = "px", Description = "Pixels a second at birth.")]
[Param("speed-variance", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How much speeds differ.")]
[Param("direction", ParamType.Float, Default = "90", Min = -360, Max = 360, Unit = "deg", Description = "Degrees clockwise from the right: -90 is up, 90 down.")]
[Param("spread", ParamType.Float, Default = "20", Min = 0, Max = 360, Unit = "deg", Description = "The fan of directions, in degrees: 360 is every way.")]
[Param("gravity", ParamType.Float2, Default = "0, 10", Unit = "px", Description = "Pixels a second a second; positive y pulls down.")]
[Param("turbulence", ParamType.Float, Default = "60", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "How far particles wander from their path.")]
[Param("turbulence-frequency", ParamType.Float, Label = "Turbulence rate", Default = "0.3", Min = 0, Max = 20, SliderMax = 5, Description = "How often the wander turns, a second.")]
[Param("size-start", ParamType.Float, Default = "5", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at birth.")]
[Param("size-end", ParamType.Float, Default = "5", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at death.")]
[Param("color-start", ParamType.Color, Default = "#FFFFFF", Description = "Colour at birth.")]
[Param("color-end", ParamType.Color, Default = "#FFFFFF", Description = "Colour at death.")]
[Param("blend", ParamType.Enum, Default = "normal", Choices = "additive, normal", Animatable = false, Description = "Add light, for fire and magic, or lay over, for snow and dust.")]
[Param("streak", ParamType.Float, Default = "0", Min = 0, Max = 1, SliderMax = 0.1, Unit = "s", Description = "Draw each particle as a line along its path this many seconds long; 0 for dots.")]
[Param("prewarm", ParamType.Bool, Default = "true", Animatable = false, Description = "Start already full, as if the field had been running.")]
public sealed class SnowGenerator : ParticleGenerator;

/// <summary>Rain streaking down at a slant.</summary>
[Generator("gen.particles.rain", Name = "Rain", Category = "Particles", Description = "Rain streaking down at a slight slant over the whole frame. A storm, a sad moment.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different field with the same settings.")]
[Param("emitter", ParamType.Enum, Default = "rectangle", Choices = "point, line, rectangle", Animatable = false, Description = "Where particles start: one point, a line as wide as the emitter, or anywhere in it.")]
[Param("position", ParamType.Point, Default = "0, -600", Unit = "px", Description = "The emitter's centre, in sequence pixels from the frame centre.")]
[Param("emitter-size", ParamType.Float2, Default = "2300, 20", Unit = "px", Description = "The emitter's width and height.")]
[Param("rate", ParamType.Float, Default = "400", Min = 0, Max = 5000, SliderMax = 500, Description = "Particles a second.")]
[Param("lifetime", ParamType.Float, Default = "1.2", Min = 0.01, Max = 60, SliderMax = 10, Unit = "s", Description = "How long each lives.")]
[Param("lifetime-variance", ParamType.Float, Default = "0.2", Min = 0, Max = 1, Description = "How much lifetimes differ.")]
[Param("speed", ParamType.Float, Default = "1400", Min = 0, Max = 10000, SliderMax = 1500, Unit = "px", Description = "Pixels a second at birth.")]
[Param("speed-variance", ParamType.Float, Default = "0.2", Min = 0, Max = 1, Description = "How much speeds differ.")]
[Param("direction", ParamType.Float, Default = "100", Min = -360, Max = 360, Unit = "deg", Description = "Degrees clockwise from the right: -90 is up, 90 down.")]
[Param("spread", ParamType.Float, Default = "4", Min = 0, Max = 360, Unit = "deg", Description = "The fan of directions, in degrees: 360 is every way.")]
[Param("gravity", ParamType.Float2, Default = "0, 0", Unit = "px", Description = "Pixels a second a second.")]
[Param("turbulence", ParamType.Float, Default = "0", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "How far particles wander from their path.")]
[Param("turbulence-frequency", ParamType.Float, Label = "Turbulence rate", Default = "1", Min = 0, Max = 20, SliderMax = 5, Description = "How often the wander turns, a second.")]
[Param("size-start", ParamType.Float, Default = "1.5", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Line width at birth.")]
[Param("size-end", ParamType.Float, Default = "1.5", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Line width at death.")]
[Param("color-start", ParamType.Color, Default = "#C8D8F080", Description = "Colour at birth.")]
[Param("color-end", ParamType.Color, Default = "#C8D8F080", Description = "Colour at death.")]
[Param("blend", ParamType.Enum, Default = "normal", Choices = "additive, normal", Animatable = false, Description = "Add light, for fire and magic, or lay over, for snow and dust.")]
[Param("streak", ParamType.Float, Default = "0.02", Min = 0, Max = 1, SliderMax = 0.1, Unit = "s", Description = "Draw each particle as a line along its path this many seconds long; 0 for dots.")]
[Param("prewarm", ParamType.Bool, Default = "true", Animatable = false, Description = "Start already full, as if the field had been running.")]
public sealed class RainGenerator : ParticleGenerator;

/// <summary>Magic sparkles spraying and floating.</summary>
[Generator("gen.particles.magic", Name = "Magic sparkles", Category = "Particles", Description = "Coloured sparkles spraying from a point and floating up as they fade. A spell, a pickup, a level up.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different field with the same settings.")]
[Param("emitter", ParamType.Enum, Default = "point", Choices = "point, line, rectangle", Animatable = false, Description = "Where particles start: one point, a line as wide as the emitter, or anywhere in it.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The emitter's centre, in sequence pixels from the frame centre.")]
[Param("emitter-size", ParamType.Float2, Default = "40, 40", Unit = "px", Description = "The emitter's width and height.")]
[Param("rate", ParamType.Float, Default = "90", Min = 0, Max = 5000, SliderMax = 500, Description = "Particles a second.")]
[Param("lifetime", ParamType.Float, Default = "2", Min = 0.01, Max = 60, SliderMax = 10, Unit = "s", Description = "How long each lives.")]
[Param("lifetime-variance", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How much lifetimes differ.")]
[Param("speed", ParamType.Float, Default = "150", Min = 0, Max = 10000, SliderMax = 1500, Unit = "px", Description = "Pixels a second at birth.")]
[Param("speed-variance", ParamType.Float, Default = "0.8", Min = 0, Max = 1, Description = "How much speeds differ.")]
[Param("direction", ParamType.Float, Default = "0", Min = -360, Max = 360, Unit = "deg", Description = "Degrees clockwise from the right: -90 is up, 90 down.")]
[Param("spread", ParamType.Float, Default = "360", Min = 0, Max = 360, Unit = "deg", Description = "The fan of directions, in degrees: 360 is every way.")]
[Param("gravity", ParamType.Float2, Default = "0, -30", Unit = "px", Description = "Pixels a second a second; negative y pulls up.")]
[Param("turbulence", ParamType.Float, Default = "80", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "How far particles wander from their path.")]
[Param("turbulence-frequency", ParamType.Float, Label = "Turbulence rate", Default = "1.2", Min = 0, Max = 20, SliderMax = 5, Description = "How often the wander turns, a second.")]
[Param("size-start", ParamType.Float, Default = "5", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at birth.")]
[Param("size-end", ParamType.Float, Default = "0", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "Diameter at death.")]
[Param("color-start", ParamType.Color, Default = "#99EEFF", Description = "Colour at birth.")]
[Param("color-end", ParamType.Color, Default = "#FF66CC", Description = "Colour at death.")]
[Param("blend", ParamType.Enum, Default = "additive", Choices = "additive, normal", Animatable = false, Description = "Add light, for fire and magic, or lay over, for snow and dust.")]
[Param("streak", ParamType.Float, Default = "0", Min = 0, Max = 1, SliderMax = 0.1, Unit = "s", Description = "Draw each particle as a line along its path this many seconds long; 0 for dots.")]
[Param("prewarm", ParamType.Bool, Default = "false", Animatable = false, Description = "Start already full, as if the field had been running.")]
public sealed class MagicGenerator : ParticleGenerator;

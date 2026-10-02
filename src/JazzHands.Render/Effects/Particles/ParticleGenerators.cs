using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JazzHands.Render.Effects.Particles;

/// <summary>
/// A particle field drawn by Direct2D: embers, sparks, dust, snow, rain or sparkles, each a
/// generator with its own defaults and the same parameters.
/// </summary>
/// <remarks>
/// The field is <see cref="ParticleField"/>, a function of the clip's time and seed, so a frame
/// is the same however it is reached. Dots are antialiased ellipses, streaks round capped lines
/// along the particle's velocity; additive fields add light, and each dot gets a faint halo.
/// Sizes and positions are sequence pixels, scaled to the working resolution.
/// </remarks>
public abstract class ParticleGenerator : VideoGenerator, ITimedGenerator
{
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

        List<Particle> particles = ParticleField.At(Settings(parameters, context.Seed), context.Time.ToSeconds());
        float scale = context.QualityScale;
        var centre = new Vector2(output.Width, output.Height) / 2.0f;
        bool additive = parameters.Enum("blend") != "normal";
        float streak = parameters.Float("streak");

        Drawing2D drawing = context.Drawing;
        drawing.Draw(output, target =>
        {
            target.PrimitiveBlend = additive ? PrimitiveBlend.Add : PrimitiveBlend.SourceOver;
            using ID2D1SolidColorBrush brush = drawing.Brush(Vector4.One);
            using ID2D1StrokeStyle round = drawing.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
            try
            {
                foreach (Particle particle in particles)
                {
                    Vector4 colour = particle.Color;
                    if (colour.W <= 0.001f)
                    {
                        continue;
                    }

                    Vector2 at = centre + (particle.Position * scale);
                    float radius = Math.Max(0.25f, particle.Diameter * scale / 2);
                    brush.Color = Straight(colour);

                    if (streak > 0)
                    {
                        target.DrawLine(at - (particle.Velocity * streak * scale), at, brush, radius * 2, round);
                        continue;
                    }

                    if (additive)
                    {
                        brush.Color = Straight(colour * 0.2f);
                        target.FillEllipse(new Ellipse(at, radius * 2.5f, radius * 2.5f), brush);
                        brush.Color = Straight(colour);
                    }

                    target.FillEllipse(new Ellipse(at, radius, radius), brush);
                }
            }
            finally
            {
                target.PrimitiveBlend = PrimitiveBlend.SourceOver;
            }
        });
    }

    /// <summary>A premultiplied colour as the straight one a brush takes.</summary>
    private static Color4 Straight(Vector4 premultiplied) => premultiplied.W > 0
        ? new Color4(premultiplied.X / premultiplied.W, premultiplied.Y / premultiplied.W, premultiplied.Z / premultiplied.W, Math.Min(1, premultiplied.W))
        : new Color4(0, 0, 0, 0);
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

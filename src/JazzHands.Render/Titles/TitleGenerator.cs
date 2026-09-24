using JazzHands.Core.Effects;
using JazzHands.Core.Titles;
using JazzHands.Render.Compositing;
using JazzHands.Render.Effects;

namespace JazzHands.Render.Titles;

/// <summary>
/// A title: styled text with an optional box, outline and shadow, placed on the frame and
/// animated by its own channels.
/// </summary>
/// <remarks>
/// The look (every parameter above <c>reveal</c>) is drawn once by <see cref="TitleRenderer"/>
/// into a layer at the working resolution and kept, so a still title costs one bitmap draw a
/// frame; the channels move, fade, scale, blur and wipe that layer. A reveal by characters, words
/// or lines draws the text again each frame it is part way.
/// </remarks>
[Generator(TitleParams.GeneratorId, Name = "Title", Category = "Titles", Description = "Text with a font, size, colour, box, outline and shadow, placed on the frame, with animations in and out; the text takes [b], [i], [u], [color=#hex], [size=n] and [font=name] tags.")]
[Param(TitleParams.Text, ParamType.Text, Default = "Title", Description = "What it says, as markup: [b]bold[/b], [i]italic[/i], [color=#FFCC00]gold[/color], [size=48]smaller[/size], \\n for a new line.")]
[Param(TitleParams.Font, ParamType.Text, Default = "Segoe UI", Animatable = false, Description = "The font family, as 'jazz fonts list' shows them; a project's fonts folder is looked in first.")]
[Param(TitleParams.Weight, ParamType.Enum, Default = "bold", Choices = "thin, extra-light, light, regular, medium, semibold, bold, extra-bold, black", Animatable = false, Description = "How heavy the letters are.")]
[Param(TitleParams.Italic, ParamType.Bool, Default = "false", Animatable = false, Description = "Slanted letters.")]
[Param(TitleParams.Size, ParamType.Float, Default = "96", Min = 1, Max = 4000, SliderMax = 400, Unit = "px", Description = "Text height, in sequence pixels.")]
[Param(TitleParams.Colour, ParamType.Color, Default = "#FFFFFF", Description = "The letters' fill.")]
[Param(TitleParams.Align, ParamType.Enum, Default = "centre", Choices = "left, centre, right", Animatable = false, Description = "How lines line up, and which edge of the text sits at the position.")]
[Param(TitleParams.VAlign, ParamType.Enum, Default = "middle", Choices = "top, middle, bottom", Animatable = false, Label = "Vertical align", Description = "Whether the top, the middle or the bottom of the text sits at the position.")]
[Param(TitleParams.Position, ParamType.Point, Default = "0, 0", Unit = "px", Description = "Where the text sits, from the frame centre; which of its edges is set by the alignments.")]
[Param(TitleParams.Width, ParamType.Float, Default = "0", Min = 0, Max = 16000, SliderMax = 3840, Unit = "px", Description = "The width lines wrap at; 0 for no wrapping.")]
[Param(TitleParams.LineSpacing, ParamType.Float, Default = "1", Min = 0.3, Max = 5, SliderMax = 3, Label = "Line spacing", Description = "Line height as a multiple of the font's own.")]
[Param(TitleParams.Tracking, ParamType.Float, Default = "0", Min = -200, Max = 1000, SliderMax = 50, Unit = "px", Label = "Letter spacing", Description = "Extra space after every letter, in sequence pixels.")]
[Param(TitleParams.Stroke, ParamType.Color, Default = "#000000", Label = "Outline", Description = "The outline's colour.")]
[Param(TitleParams.StrokeWidth, ParamType.Float, Default = "0", Min = 0, Max = 200, SliderMax = 20, Unit = "px", Label = "Outline width", Description = "How far the outline reaches outside the letters; 0 for none.")]
[Param(TitleParams.Box, ParamType.Color, Default = "#00000000", Description = "A box behind the text; transparent for none.")]
[Param(TitleParams.BoxPadding, ParamType.Float, Default = "24", Min = 0, Max = 2000, SliderMax = 200, Unit = "px", Label = "Box padding", Description = "Space between the text and the box's edge.")]
[Param(TitleParams.BoxRadius, ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 100, Unit = "px", Label = "Box corners", Description = "How round the box's corners are.")]
[Param(TitleParams.Shadow, ParamType.Color, Default = "#00000000", Description = "A soft shadow under the letters; transparent for none.")]
[Param(TitleParams.ShadowOffset, ParamType.Float2, Default = "4, 4", Unit = "px", Label = "Shadow offset", Description = "How far the shadow falls, across and down, in sequence pixels.")]
[Param(TitleParams.ShadowBlur, ParamType.Float, Default = "8", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Label = "Shadow softness", Description = "How soft the shadow's edge is.")]
[Param(TitleParams.Reveal, ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How much of the text shows, 0 to 1: the typewriter, word and wipe animations drive it.")]
[Param(TitleParams.RevealBy, ParamType.Enum, Default = "characters", Choices = "characters, words, lines, wipe", Label = "Reveal by", Description = "What the reveal counts in: letters, words or lines appearing in turn, or a soft edge sweeping across.")]
[Param(TitleParams.RevealSoft, ParamType.Float, Default = "0", Min = 0, Max = 20, SliderMax = 10, Label = "Reveal softness", Description = "How many letters, words or lines fade in at once; for a wipe, the soft edge in tenths of the width.")]
[Param(TitleParams.Fade, ParamType.Float, Default = "1", Min = 0, Max = 1, Label = "Title opacity", Description = "How visible the title is, apart from the clip's opacity: the fade animations drive it.")]
[Param(TitleParams.Offset, ParamType.Float2, Default = "0, 0", Unit = "px", Description = "A move away from the position, in sequence pixels: the slide animations drive it.")]
[Param(TitleParams.Zoom, ParamType.Float, Default = "1", Min = 0, Max = 20, SliderMax = 3, Description = "A scale about the text's centre: the scale animation drives it.")]
[Param(TitleParams.Blur, ParamType.Float, Default = "0", Min = 0, Max = 500, SliderMax = 100, Unit = "px", Description = "A blur over the whole title: the blur animation drives it.")]
[Param(TitleParams.AnimationIn, ParamType.Enum, Default = "none", Choices = "none, fade, slide-left, slide-right, slide-up, slide-down, scale, typewriter, word-reveal, blur, wipe", Animatable = false, Label = "Animation in", Description = "The animation it comes in with, as last chosen by title.set-animation; the keyframes it made do the work.")]
[Param(TitleParams.AnimationOut, ParamType.Enum, Default = "none", Choices = "none, fade, slide-left, slide-right, slide-up, slide-down, scale, typewriter, word-reveal, blur, wipe", Animatable = false, Label = "Animation out", Description = "The animation it goes out with, as last chosen by title.set-animation.")]
public sealed class TitleGenerator : VideoGenerator
{
    private TitleRenderer? _renderer;

    /// <summary>How many looks are drawn and kept.</summary>
    public int Cached => _renderer?.Cached ?? 0;

    /// <summary>How many times a look has been drawn rather than found kept.</summary>
    public int Drawn => _renderer?.Drawn ?? 0;

    /// <summary>
    /// Where a title's text sits on a frame of the sequence's size, in sequence pixels from its
    /// centre, laid out exactly as it is drawn; null when it has no text.
    /// </summary>
    /// <param name="parameters">The title's parameters at the moment asked about.</param>
    /// <param name="frame">The sequence's size.</param>
    /// <param name="projectFolder">The project's folder, for its fonts; empty for none.</param>
    public static TitleBounds? Measure(ParameterSet parameters, System.Numerics.Vector2 frame, string projectFolder) =>
        TitleLayout.Measure(parameters, frame, projectFolder);

    /// <inheritdoc />
    public override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        _renderer ??= new TitleRenderer(context.Drawing, context.Pool);
        _renderer.Render(context, parameters, output);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer?.Dispose();
            _renderer = null;
        }

        base.Dispose(disposing);
    }
}

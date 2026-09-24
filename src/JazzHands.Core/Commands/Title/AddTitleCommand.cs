using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a title on the timeline, styled by a preset with any of its look changed.</summary>
/// <remarks>
/// A title is a <c>gen.title</c> generator clip. The preset (<c>title.list-presets</c>) gives its
/// look, place, length and animations, scaled from 1080 lines to the sequence; the options change
/// what they name. Without a track it goes on the highest video track free for its length, or on
/// a new track above the others when none is. Every option is a parameter of <c>gen.title</c>
/// that <c>title.set-style</c> and <c>param.set</c> change later.
/// </remarks>
/// <param name="At">Where it starts on the timeline.</param>
/// <param name="Text">The text, as markup: <c>[b]</c>, <c>[i]</c>, <c>[u]</c>, <c>[color=#hex]</c>, <c>[size=n]</c>, <c>[font=name]</c>, <c>\n</c> for a new line.</param>
/// <param name="Preset">Which preset. Defaults to title-card.</param>
/// <param name="Duration">How long it lasts. Defaults to the preset's.</param>
/// <param name="TrackId">Which video track.</param>
/// <param name="Font">The font family.</param>
/// <param name="Size">Text height in sequence pixels.</param>
/// <param name="Color">The fill colour.</param>
/// <param name="Align">left, centre or right.</param>
/// <param name="Box">The box behind the text, as a colour; transparent for none.</param>
/// <param name="Shadow">The shadow, as a colour; transparent for none.</param>
/// <param name="Stroke">The outline: a width in pixels, then optionally a colour.</param>
/// <param name="AnimationIn">What it comes in with.</param>
/// <param name="AnimationOut">What it goes out with.</param>
/// <param name="Name">Its display name. Defaults to its text.</param>
/// <param name="ClipId">The identifier to give it.</param>
/// <param name="SequenceId">Which sequence, when no track is named. Defaults to the active one.</param>
[Command("title.add", Description = "Put a title on the timeline from a preset")]
public sealed record AddTitleCommand(
    [property: Option("at", "Where it starts on the timeline")] Flicks At,
    [property: Option("text", "The text, as markup: [b]bold[/b], [color=#FFCC00]gold[/color], \\n for a new line")] string? Text = null,
    [property: Option("preset", "Which preset, as title list-presets shows them; title-card when left out")] string? Preset = null,
    [property: Option("dur", "How long it lasts; the preset's when left out")] Flicks? Duration = null,
    [property: Option("track", "Which video track; the highest free one when left out")] string? TrackId = null,
    [property: Option("font", "The font family, as fonts list shows them")] string? Font = null,
    [property: Option("size", "Text height in sequence pixels")] string? Size = null,
    [property: Option("color", "The fill colour, for example #FFFFFF")] string? Color = null,
    [property: Option("align", "left, centre or right")] string? Align = null,
    [property: Option("box", "The box behind the text as a colour, for example #00000099; #00000000 for none")] string? Box = null,
    [property: Option("shadow", "The shadow as a colour, for example #000000A0; #00000000 for none")] string? Shadow = null,
    [property: Option("stroke", "The outline: a width in pixels, then optionally a colour, as '4' or '4 #000000'")] string? Stroke = null,
    [property: Option("anim-in", "What it comes in with: none, fade, slide-left, typewriter, word-reveal and more")] string? AnimationIn = null,
    [property: Option("anim-out", "What it goes out with, from the same list")] string? AnimationOut = null,
    [property: Option("name", "Its display name; its text when left out")] string? Name = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null,
    [property: Option("sequence", "Which sequence, when no track is named")] string? SequenceId = null) : ICommand;

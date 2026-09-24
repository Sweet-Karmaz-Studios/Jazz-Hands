using System.Globalization;
using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Titles;
using JazzHands.Render.Titles;

namespace JazzHands.Engine.Handlers;

/// <summary>What the title handlers share: finding a title, reading its options and storing its parameters.</summary>
internal static class TitleHelp
{
    /// <summary>The title generator's description.</summary>
    internal static EffectDescriptor Descriptor => EffectCatalog.Registry.Find(TitleParams.GeneratorId)
        ?? throw new InvalidOperationException("The title generator is not in the effect registry.");

    /// <summary>A title clip, unlocked, or a refusal.</summary>
    internal static ClipLocation Title(Project project, string clipId)
    {
        ClipLocation location = HandlerHelp.Clip(project, clipId);
        if (!string.Equals(location.Clip.GeneratorId, TitleParams.GeneratorId, StringComparison.Ordinal))
        {
            throw new CommandException(
                "not-a-title",
                $"'{location.Clip.Name}' ({clipId}) is not a title. Titles are made with 'jazz title add'.");
        }

        HandlerHelp.RequireUnlocked(location.Track);
        return location;
    }

    /// <summary>A title's own parameters, empty when it has none set.</summary>
    internal static Effect Own(Clip clip) =>
        clip.Effects.FirstOrDefault(effect => EffectChains.IsOwnParameters(clip, effect)) ?? Effect.Create(TitleParams.GeneratorId);

    /// <summary>The project with a title's own parameters replaced.</summary>
    internal static Project Store(Project project, ClipLocation location, Effect own, HandlerContext context, Clip? changed = null)
    {
        Clip clip = changed ?? location.Clip;
        int index = clip.Effects.IndexOf(effect => EffectChains.IsOwnParameters(clip, effect));
        EquatableArray<Effect> effects = index < 0 ? clip.Effects.Insert(0, own) : clip.Effects.SetItem(index, own);

        context.Changed(clip.Id);
        return project.ReplaceTrack(location.Track.ReplaceClip(clip with { Effects = effects }));
    }

    /// <summary>A copy with one parameter set from typed text, refused when it does not parse or is keyframed.</summary>
    internal static Effect Set(Effect own, string name, string? text, bool refuseAnimated = true)
    {
        if (text is null)
        {
            return own;
        }

        ParamDescriptor descriptor = Descriptor.Param(name)!;
        if (refuseAnimated && own.Parameter(name) is KeyframedValue { IsAnimated: true })
        {
            throw new CommandException(
                "param-animated",
                $"'{name}' has keyframes, so it has no one value to set. Change a keyframe with 'jazz param set <clip> {name} <value> --at <time>', or turn animation off with 'jazz param clear-keyframes'.");
        }

        ParamValue value = ParamHelp.Value(descriptor, Normalise(name, text));
        return own.WithParameter(name, AnimatedValue.Constant(value));
    }

    /// <summary>
    /// The outline option: a width, or a width and a colour in either order. Only what is given
    /// changes.
    /// </summary>
    internal static Effect Stroke(Effect own, string? text)
    {
        if (text is null)
        {
            return own;
        }

        string? width = null;
        string? colour = null;
        foreach (string part in text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith('#'))
            {
                colour = part;
            }
            else if (float.TryParse(part.TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                width = part.TrimEnd('p', 'x');
            }
            else
            {
                throw new CommandException("invalid-value", $"--stroke takes a width and optionally a colour, as '4' or '4 #000000', not '{text}'.");
            }
        }

        return Set(Set(own, TitleParams.StrokeWidth, width), TitleParams.Stroke, colour);
    }

    /// <summary>The sequence's frame size, which presets and animations scale to.</summary>
    internal static Vector2 Frame(Project project, Sequence sequence)
    {
        ProjectSettings settings = project.SettingsFor(sequence);
        return new Vector2(settings.Width, settings.Height);
    }

    /// <summary>A title's name from its text: the first line, shortened.</summary>
    internal static string NameFor(string markup)
    {
        string plain = TitleMarkup.PlainText(markup);
        string line = plain.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return line.Length == 0 ? "Title" : line.Length > 40 ? line[..39] + "…" : line;
    }

    /// <summary>Accepts the American spellings of the alignments as well.</summary>
    private static string Normalise(string name, string text)
    {
        string trimmed = text.Trim();
        return name switch
        {
            TitleParams.Align when trimmed.Equals("center", StringComparison.OrdinalIgnoreCase) => "centre",
            TitleParams.VAlign when trimmed.Equals("center", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("centre", StringComparison.OrdinalIgnoreCase) => "middle",
            _ => text,
        };
    }
}

/// <summary>Puts a title on the timeline from a preset.</summary>
public sealed class AddTitleHandler : ICommandHandler<AddTitleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddTitleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        TitlePreset preset = TitlePresetLibrary.Require(command.Preset);
        Flicks duration = command.Duration ?? preset.DefaultLength;
        if (duration <= Flicks.Zero)
        {
            throw new CommandException("empty-clip", "A title needs a duration greater than zero.");
        }

        if (command.At < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "A title cannot start before the timeline does.");
        }

        string id = HandlerHelp.IdOr(command.ClipId);
        HandlerHelp.RequireUnused(project, id);
        var range = new TimeRange(command.At, duration);

        (Sequence sequence, Track track, bool made) = Place(project, command, range);
        Vector2 frame = TitleHelp.Frame(project, sequence);

        string text = command.Text ?? TitleMarkup.ScaleSizes(preset.Text, frame.Y / TitlePreset.ReferenceHeight);
        Effect own = preset.Style(Effect.Create(TitleParams.GeneratorId), TitleHelp.Descriptor, frame);
        own = TitleHelp.Set(own, TitleParams.Text, text);
        own = TitleHelp.Set(own, TitleParams.Font, command.Font);
        own = TitleHelp.Set(own, TitleParams.Size, command.Size);
        own = TitleHelp.Set(own, TitleParams.Colour, command.Color);
        own = TitleHelp.Set(own, TitleParams.Align, command.Align);
        own = TitleHelp.Set(own, TitleParams.Box, command.Box);
        own = TitleHelp.Set(own, TitleParams.Shadow, command.Shadow);
        own = TitleHelp.Stroke(own, command.Stroke);

        TitleAnimation animation = preset.AnimationFor();
        animation = animation with
        {
            In = Animation(command.AnimationIn) ?? animation.In,
            Out = Animation(command.AnimationOut) ?? animation.Out,
        };
        own = TitleAnimations.Apply(own, duration, frame, animation);

        var clip = new Clip(
            id,
            range,
            Flicks.Zero,
            GeneratorId: TitleParams.GeneratorId,
            Effects: EquatableArray.Create(own),
            Name: command.Name ?? TitleHelp.NameFor(text));

        context.Changed(id);
        context.Changed(track.Id);
        Sequence updated = made ? sequence.AddTrack(track.AddClip(clip)) : sequence.ReplaceTrack(track.AddClip(clip));
        if (made)
        {
            context.Changed(sequence.Id);
        }

        return project.ReplaceSequence(updated);
    }

    /// <summary>A name given for an animation, checked, or null when none was.</summary>
    internal static string? Animation(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string trimmed = name.Trim().ToLowerInvariant();
        return TitleAnimations.IsKnown(trimmed)
            ? trimmed
            : throw new CommandException("invalid-value", $"'{name}' is not an animation. There are {string.Join(", ", TitleAnimations.Names)}.");
    }

    /// <summary>
    /// The track a title goes on: the one named, or the highest video track free for its whole
    /// length, or a new video track above everything when none is.
    /// </summary>
    private static (Sequence Sequence, Track Track, bool Made) Place(Project project, AddTitleCommand command, TimeRange range)
    {
        var probe = new Clip("probe", range, Flicks.Zero);

        if (command.TrackId is { Length: > 0 } trackId)
        {
            (Sequence on, Track named) = HandlerHelp.Track(project, trackId);
            HandlerHelp.RequireUnlocked(named);
            if (named.Kind != TrackKind.Video)
            {
                throw new CommandException("wrong-track-kind", $"'{named.Name}' is not a video track. A title goes on a video track.");
            }

            if (EditOps.Overlaps(named, probe))
            {
                throw new CommandException(
                    "would-overlap",
                    $"A clip already occupies that part of '{named.Name}'. Leave --track out to use a free track, or pick another time.");
            }

            return (on, named, false);
        }

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        Track? free = sequence.Tracks
            .Where(track => track.Kind == TrackKind.Video && !track.Locked)
            .OrderByDescending(track => track.Order)
            .FirstOrDefault(track => !EditOps.Overlaps(track, probe));

        if (free is not null)
        {
            return (sequence, free, false);
        }

        var made = new Track(Id.New(), TrackKind.Video, HandlerHelp.TrackName(sequence, TrackKind.Video), sequence.NextTrackOrder());
        return (sequence, made, true);
    }
}

/// <summary>Changes what a title says.</summary>
public sealed class SetTitleTextHandler : ICommandHandler<SetTitleTextCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTitleTextCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation location = TitleHelp.Title(project, command.ClipId);
        Effect own = TitleHelp.Own(location.Clip);
        string before = own.Parameter(TitleParams.Text) is StaticValue { Value: ParamValue.Text { Value: var old } } ? old : "Title";
        string text = command.Plain ? TitleMarkup.Escape(command.Text) : command.Text ?? string.Empty;
        own = TitleHelp.Set(own, TitleParams.Text, text);

        // A title still named after its text follows the text; one someone named keeps its name.
        Clip clip = location.Clip;
        if (string.Equals(clip.Name, TitleHelp.NameFor(before), StringComparison.Ordinal) || clip.Name.Length == 0)
        {
            clip = clip with { Name = TitleHelp.NameFor(text) };
        }

        return TitleHelp.Store(project, location, own, context, clip);
    }
}

/// <summary>Changes how a title looks.</summary>
public sealed class SetTitleStyleHandler : ICommandHandler<SetTitleStyleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTitleStyleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation location = TitleHelp.Title(project, command.ClipId);
        Effect own = TitleHelp.Own(location.Clip);

        if (command.Preset is { } name)
        {
            TitlePreset preset = TitlePresetLibrary.Require(name);
            foreach (string param in preset.Values.Keys)
            {
                if (own.Parameter(param) is KeyframedValue { IsAnimated: true })
                {
                    throw new CommandException("param-animated", $"'{param}' has keyframes, which the preset '{preset.Name}' would replace. Clear them first with 'jazz param clear-keyframes {location.Clip.Id} {param}'.");
                }
            }

            own = preset.Style(own, TitleHelp.Descriptor, TitleHelp.Frame(project, location.Sequence));
        }

        own = TitleHelp.Set(own, TitleParams.Font, command.Font);
        own = TitleHelp.Set(own, TitleParams.Weight, command.Weight);
        own = TitleHelp.Set(own, TitleParams.Italic, command.Italic is { } italic ? (italic ? "true" : "false") : null);
        own = TitleHelp.Set(own, TitleParams.Size, command.Size);
        own = TitleHelp.Set(own, TitleParams.Colour, command.Color);
        own = TitleHelp.Set(own, TitleParams.Align, command.Align);
        own = TitleHelp.Set(own, TitleParams.VAlign, command.VAlign);
        own = TitleHelp.Set(own, TitleParams.Position, command.Position);
        own = TitleHelp.Set(own, TitleParams.Width, command.Width);
        own = TitleHelp.Set(own, TitleParams.LineSpacing, command.LineSpacing);
        own = TitleHelp.Set(own, TitleParams.Tracking, command.Tracking);
        own = TitleHelp.Stroke(own, command.Stroke);
        own = TitleHelp.Set(own, TitleParams.Box, command.Box);
        own = TitleHelp.Set(own, TitleParams.BoxPadding, command.BoxPadding);
        own = TitleHelp.Set(own, TitleParams.BoxRadius, command.BoxRadius);
        own = TitleHelp.Set(own, TitleParams.Shadow, command.Shadow);
        own = TitleHelp.Set(own, TitleParams.ShadowOffset, command.ShadowOffset);
        own = TitleHelp.Set(own, TitleParams.ShadowBlur, command.ShadowBlur);

        return TitleHelp.Store(project, location, own, context);
    }
}

/// <summary>Chooses how a title comes in and goes out.</summary>
public sealed class SetTitleAnimationHandler : ICommandHandler<SetTitleAnimationCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTitleAnimationCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.In is null && command.Out is null && command.InDuration is null && command.OutDuration is null)
        {
            throw new CommandException("missing-value", "Give --in, --out or a duration to change.");
        }

        if (command.InDuration is { } inLength && inLength <= Flicks.Zero || command.OutDuration is { } outLength && outLength <= Flicks.Zero)
        {
            throw new CommandException("invalid-value", "An animation takes longer than no time at all.");
        }

        ClipLocation location = TitleHelp.Title(project, command.ClipId);
        Effect own = TitleHelp.Own(location.Clip);
        TitleAnimation now = TitleAnimations.Read(own, location.Clip.Duration);
        var wanted = new TitleAnimation(
            AddTitleHandler.Animation(command.In) ?? now.In,
            command.InDuration ?? now.InDuration,
            AddTitleHandler.Animation(command.Out) ?? now.Out,
            command.OutDuration ?? now.OutDuration);

        own = TitleAnimations.Apply(own, location.Clip.Duration, TitleHelp.Frame(project, location.Sequence), wanted);
        return TitleHelp.Store(project, location, own, context);
    }
}

/// <summary>Lists the title presets.</summary>
public sealed class ListTitlePresetsHandler : IQueryHandler<ListTitlePresetsQuery, TitlePresetInfo[]>
{
    /// <inheritdoc />
    public TitlePresetInfo[] Handle(Project project, ListTitlePresetsQuery query, QueryContext context) =>
        [.. TitlePresetLibrary.All.Select(TitlePresetLibrary.Info)];
}

/// <summary>Lists the font families titles can use.</summary>
public sealed class ListFontsHandler : IQueryHandler<ListFontsQuery, FontInfo[]>
{
    /// <inheritdoc />
    public FontInfo[] Handle(Project project, ListFontsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        string folder = context.Session?.ProjectPath is { Length: > 0 } path ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty : string.Empty;
        return
        [
            .. FontCatalog.Families(folder)
                .Where(family => query.Search is not { Length: > 0 } search || family.Family.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(family => new FontInfo(family.Family, family.IsProject ? "project" : "system", family.Faces)),
        ];
    }
}

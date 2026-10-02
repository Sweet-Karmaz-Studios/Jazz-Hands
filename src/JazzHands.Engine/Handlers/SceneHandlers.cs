using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;
using JazzHands.Render.Scene;

namespace JazzHands.Engine.Handlers;

/// <summary>What the 3D handlers share (Phase 47).</summary>
internal static class SceneHelp
{
    /// <summary>How long a camera or a light lasts when nobody says: to the end of the sequence, and at least this.</summary>
    internal static readonly Flicks ShortestDefault = Flicks.FromSeconds(10);

    /// <summary>
    /// Puts a camera or a light on the timeline with its own parameters, on the track asked for or
    /// a free one above every picture.
    /// </summary>
    internal static Project Add(
        Project project,
        HandlerContext context,
        string type,
        Flicks? at,
        Flicks? duration,
        string? trackId,
        string? sequenceId,
        string? clipId,
        string name,
        IEnumerable<(string Name, string? Text)> values,
        Layer3D? space = null)
    {
        Flicks start = at ?? Flicks.Zero;
        if (start < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "It cannot start before the timeline does.", "at");
        }

        Sequence target = trackId is { Length: > 0 } named ? HandlerHelp.Track(project, named).Sequence : HandlerHelp.Sequence(project, sequenceId);
        Flicks length = duration ?? Flicks.Max(target.Duration - start, ShortestDefault);
        if (length <= Flicks.Zero)
        {
            throw new CommandException("empty-clip", "It needs a duration greater than zero.", "dur");
        }

        string id = HandlerHelp.IdOr(clipId);
        HandlerHelp.RequireUnused(project, id);

        EffectDescriptor descriptor = EffectCatalog.Registry.Find(type)
            ?? throw new InvalidOperationException($"The effect registry has no '{type}'.");
        Effect own = Effect.Create(type);
        foreach ((string parameter, string? text) in values)
        {
            if (text is null)
            {
                continue;
            }

            ParamDescriptor known = descriptor.Param(parameter) ?? throw new InvalidOperationException($"'{type}' has no parameter '{parameter}'.");
            ParamValue value;
            try
            {
                value = ParamHelp.Value(known, text);
            }
            catch (CommandException refused)
            {
                // The option's own name is what the person typed.
                throw new CommandException(refused.Code, refused.Message, parameter);
            }

            if (value != known.Default)
            {
                own = own.WithParameter(parameter, AnimatedValue.Constant(value));
            }
        }

        var range = new TimeRange(start, length);
        (Sequence sequence, Track track, bool made) = HandlerHelp.FreeVideoTrack(project, trackId, sequenceId, range, name);
        var clip = new Clip(
            id,
            range,
            Flicks.Zero,
            GeneratorId: type,
            Effects: own.Parameters.IsEmpty ? default : EquatableArray.Create(own),
            Name: name,
            Layer3D: space);

        context.Changed(id);
        context.Changed(track.Id);
        Sequence updated = made ? sequence.AddTrack(track.AddClip(clip)) : sequence.ReplaceTrack(track.AddClip(clip));
        if (made)
        {
            context.Changed(sequence.Id);
        }

        return project.ReplaceSequence(updated);
    }

    /// <summary>Geometry throws shadows unless somebody says otherwise.</summary>
    internal static Layer3D Solid { get; } = Layer3D.Default with { CastsShadows = true };

    /// <summary>A number option as the text a parameter parses, or null when it was not given.</summary>
    internal static string? Text(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A switch option as text, or null when it is off (the parameters' default).</summary>
    internal static string? Switch(bool on) => on ? "true" : null;
}

/// <summary>Makes a clip a 3D layer, or flat again.</summary>
public sealed class SetClip3DHandler : ICommandHandler<SetClip3DCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClip3DCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;

        if (found.Track.Kind != TrackKind.Video || SceneObjects.Is(clip))
        {
            throw new CommandException(
                "not-a-picture",
                found.Track.Kind == TrackKind.Adjustment
                    ? $"'{clip.Name}' is on an adjustment track. Only a picture on a video track can be a 3D layer."
                    : $"'{clip.Name}' has no picture to set in 3D. Only a picture on a video track can be a 3D layer.",
                "clipId");
        }

        if (command.Off && (command.Lights is not null || command.CastsShadows is not null || command.AcceptsShadows is not null))
        {
            throw new CommandException("invalid-value", "--off makes the clip flat; give the material switches without it.", "off");
        }

        Layer3D? space = command.Off
            ? null
            : (clip.Layer3D ?? Layer3D.Default) with
            {
                AcceptsLights = command.Lights ?? clip.Layer3D?.AcceptsLights ?? Layer3D.Default.AcceptsLights,
                CastsShadows = command.CastsShadows ?? clip.Layer3D?.CastsShadows ?? Layer3D.Default.CastsShadows,
                AcceptsShadows = command.AcceptsShadows ?? clip.Layer3D?.AcceptsShadows ?? Layer3D.Default.AcceptsShadows,
            };

        if (space == clip.Layer3D)
        {
            return project;
        }

        context.Changed(clip.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Layer3D = space }));
    }
}

/// <summary>Puts a 3D camera on the timeline.</summary>
public sealed class AddCameraHandler : ICommandHandler<AddCameraCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddCameraCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return SceneHelp.Add(
            project,
            context,
            SceneObjects.Camera,
            command.At,
            command.Duration,
            command.TrackId,
            command.SequenceId,
            command.ClipId,
            command.Name ?? "Camera",
            [
                ("dolly", SceneHelp.Text(command.Dolly)),
                ("orbit", SceneHelp.Text(command.Orbit)),
                ("tilt", SceneHelp.Text(command.Tilt)),
                ("roll", SceneHelp.Text(command.Roll)),
                ("angle", SceneHelp.Text(command.Angle)),
                ("position", command.Position),
                ("target", command.Target),
                ("target-z", SceneHelp.Text(command.TargetZ)),
                ("depth-of-field", SceneHelp.Switch(command.DepthOfField)),
                ("focus", SceneHelp.Text(command.Focus)),
                ("aperture", SceneHelp.Text(command.Aperture)),
            ]);
    }
}

/// <summary>Puts a 3D light on the timeline.</summary>
public sealed class AddLightHandler : ICommandHandler<AddLightCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddLightCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string kind = command.Kind.Trim().ToLowerInvariant();
        if (kind is not ("point" or "spot" or "directional" or "ambient" or "environment"))
        {
            throw new CommandException("invalid-value", $"'{command.Kind}' is not a kind of light. There are point, spot, directional, ambient and environment.", "kind");
        }

        if (command.Shadows && kind is "ambient" or "environment")
        {
            throw new CommandException("invalid-value", $"An {kind} light comes from everywhere, so it casts no shadows.", "shadows");
        }

        if (command.Image is { Length: > 0 } image && project.MediaItem(image) is null)
        {
            throw new CommandException("media-not-found", $"There is no media item '{image}' for the environment. Import the still first and give its id.", "image");
        }

        return SceneHelp.Add(
            project,
            context,
            SceneObjects.Light,
            command.At,
            command.Duration,
            command.TrackId,
            command.SequenceId,
            command.ClipId,
            command.Name ?? $"{char.ToUpperInvariant(kind[0])}{kind[1..]} light",
            [
                ("kind", kind),
                ("color", command.Color),
                ("intensity", SceneHelp.Text(command.Intensity)),
                ("position", command.Position),
                ("position-z", SceneHelp.Text(command.Z)),
                ("target", command.Target),
                ("target-z", SceneHelp.Text(command.TargetZ)),
                ("cone-angle", SceneHelp.Text(command.Cone)),
                ("cone-feather", SceneHelp.Text(command.Feather)),
                ("falloff", command.Falloff),
                ("radius", SceneHelp.Text(command.Radius)),
                ("casts-shadows", SceneHelp.Switch(command.Shadows)),
                ("shadow-softness", SceneHelp.Text(command.Softness)),
                ("shadow-darkness", SceneHelp.Text(command.Darkness)),
                ("image", command.Image),
            ]);
    }
}

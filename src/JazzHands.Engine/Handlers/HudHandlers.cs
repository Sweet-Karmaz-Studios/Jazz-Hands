using System.Globalization;
using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Analysis;

namespace JazzHands.Engine.Handlers;

/// <summary>What finding and hiding a HUD share.</summary>
internal static class HudHelp
{
    /// <summary>The still regions of a media clip, in its source pixels.</summary>
    internal static StaticRegionInfo[] Find(Project project, string clipId, int samples, string projectPath)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);
        Clip clip = found.Clip;
        if (clip.MediaId is not { } mediaId || found.Track.Kind != TrackKind.Video)
        {
            throw new CommandException("not-media", "Only a clip of a video file has a picture to look for a HUD in.");
        }

        if (samples is < 2 or > 240)
        {
            throw new CommandException("invalid-value", "Compare 2 to 240 frames.", "samples");
        }

        MediaItem item = MediaServices.Require(project, mediaId);
        string path = HandlerHelp.Resolve(projectPath, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}. Relink it first.");
        }

        return
        [
            .. StaticFinder.Find(path, clip.SourceIn, clip.SourceDuration, samples)
                .Select(region => new StaticRegionInfo(region.X, region.Y, region.Width, region.Height, region.Score)),
        ];
    }

    /// <summary>Regions as <c>--regions</c> writes them.</summary>
    internal static StaticRegionInfo[] Parse(string text)
    {
        var regions = new List<StaticRegionInfo>();
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] numbers = part.Split(',', StringSplitOptions.TrimEntries);
            if (numbers.Length != 4
                || !int.TryParse(numbers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(numbers[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(numbers[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                || !int.TryParse(numbers[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
                || width <= 0 || height <= 0)
            {
                throw new CommandException("invalid-value", $"'{part}' is not a region; write x,y,width,height in source pixels, several separated by ;.", "regions");
            }

            regions.Add(new StaticRegionInfo(x, y, width, height, 1));
        }

        return [.. regions];
    }

    /// <summary>A rectangle mask over a region, softened a little at its edge.</summary>
    internal static Mask Mask(StaticRegionInfo region) => new(
        Id.New(),
        MaskShape.Rectangle,
        Bounds: AnimatedValue.Constant(new ParamValue.Float4(new Vector4(region.X, region.Y, region.Width, region.Height))),
        Feather: AnimatedValue.Constant(4.0f));
}

/// <summary>Finds a clip's still regions.</summary>
public sealed class FindStaticHandler : IQueryHandler<FindStaticQuery, StaticRegionInfo[]>
{
    /// <inheritdoc />
    public StaticRegionInfo[] Handle(Project project, FindStaticQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        return HudHelp.Find(project, query.ClipId, query.Samples, context.Session?.ProjectPath ?? string.Empty);
    }
}

/// <summary>Hides a clip's still regions.</summary>
public sealed class HideStaticHandler : ICommandHandler<HideStaticCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, HideStaticCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        if (!Enum.IsDefined(command.How))
        {
            throw new CommandException("invalid-value", $"{(int)command.How} is not a way to hide.", "how");
        }

        StaticRegionInfo[] regions = command.Regions is { Length: > 0 } text
            ? HudHelp.Parse(text)
            : HudHelp.Find(project, clip.Id, 24, context.ProjectPath);
        if (regions.Length == 0)
        {
            throw new CommandException("nothing-still", $"Nothing stands still over '{clip.Name}' to hide; give --regions to hide a place anyway.");
        }

        context.Changed(clip.Id);
        switch (command.How)
        {
            case HideHow.Blur:
            case HideHow.Pixelate:
            {
                Effect effect = command.How == HideHow.Blur
                    ? Effect.Create("video.blur.gaussian").WithParameter("radius", AnimatedValue.Constant(30.0f))
                    : Effect.Create("video.pixelate").WithParameter("size", AnimatedValue.Constant(24.0f));
                effect = effect with { Masks = [.. regions.Select(HudHelp.Mask)] };
                context.Changed(effect.Id);
                return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = [.. clip.Effects, effect] }));
            }

            case HideHow.Fill:
                return Fill(project, found, regions, command.Plate, context);

            default:
                return Crop(project, found, regions);
        }
    }

    /// <summary>A frozen clean frame of the same clip on a track above, masked to the regions.</summary>
    private static Project Fill(Project project, ClipLocation found, StaticRegionInfo[] regions, Flicks? plate, HandlerContext context)
    {
        Clip clip = found.Clip;
        if (plate is not { } at)
        {
            throw new CommandException("plate-required", "Fill needs --plate: a moment on the sequence, inside the clip, where the HUD is not shown.", "plate");
        }

        if (at < clip.Start || at >= clip.End || clip.MediaId is null)
        {
            throw new CommandException("time-out-of-range", "The clean frame has to be a moment inside the clip.", "plate");
        }

        var cover = new Clip(
            Id.New(),
            clip.Range,
            clip.SourceTimeAt(at),
            MediaId: clip.MediaId,
            SourceStreamIndex: clip.SourceStreamIndex,
            Name: $"{clip.Name} (clean plate)",
            Hold: true)
        {
            Transform = clip.Transform,
            Crop = clip.Crop,
            Masks = [.. regions.Select(HudHelp.Mask)],
        };

        var above = new Track(Id.New(), TrackKind.Video, HandlerHelp.TrackName(found.Sequence, TrackKind.Video), found.Track.Order + 1, [cover]);
        Sequence sequence = found.Sequence;
        Sequence updated = TrackOrder.Renumber(sequence.AddTrack(above), above.Id, above.Order, context);
        context.Changed(cover.Id);
        context.Changed(above.Id);
        return project.ReplaceSequence(updated);
    }

    /// <summary>The regions cut off the nearest edges, and the picture scaled up to fill the frame.</summary>
    private static Project Crop(Project project, ClipLocation found, StaticRegionInfo[] regions)
    {
        Clip clip = found.Clip;
        MediaStream? stream = clip.MediaId is { } mediaId ? project.MediaItem(mediaId)?.Info?.Streams.FirstOrDefault(candidate => candidate.Index == clip.SourceStreamIndex) : null;
        if (stream is not { Width: > 0, Height: > 0 })
        {
            throw new CommandException("not-media", "Cropping needs the clip's picture size; probe its media first.");
        }

        // Each region goes off whichever edge takes least of the picture with it, as fractions.
        float left = 0, top = 0, right = 0, bottom = 0;
        foreach (StaticRegionInfo region in regions)
        {
            float x0 = region.X / (float)stream.Width;
            float y0 = region.Y / (float)stream.Height;
            float x1 = (region.X + region.Width) / (float)stream.Width;
            float y1 = (region.Y + region.Height) / (float)stream.Height;
            float least = MathF.Min(MathF.Min(x1, 1 - x0), MathF.Min(y1, 1 - y0));
            if (least == 1 - y0)
            {
                bottom = MathF.Max(bottom, 1 - y0);
            }
            else if (least == y1)
            {
                top = MathF.Max(top, y1);
            }
            else if (least == x1)
            {
                left = MathF.Max(left, x1);
            }
            else
            {
                right = MathF.Max(right, 1 - x0);
            }
        }

        if (left + right > 0.5f || top + bottom > 0.5f)
        {
            throw new CommandException("too-much", "Cropping those regions off would lose more than half the picture; blur, pixelate or fill them instead.");
        }

        // Scaled so the kept part covers the frame, and moved so its middle is the frame's.
        float scale = MathF.Max(1 / (1 - left - right), 1 / (1 - top - bottom));
        ProjectSettings settings = project.SettingsFor(found.Sequence);
        Vector2 fitted = Vector2.Abs(Vector2.TransformNormal(
            new Vector2(stream.Width, stream.Height),
            Render.Compositing.RenderGraphBuilder.SourcePlacement(project, clip with { Transform = Transform.Identity }, default, new Vector2(settings.Width, settings.Height))));
        var shift = new Vector2((right - left) / 2 * fitted.X * scale, (bottom - top) / 2 * fitted.Y * scale);

        Clip cropped = clip with
        {
            Crop = new Crop(
                AnimatedValue.Constant(MathF.Round(left * 100, 3)),
                AnimatedValue.Constant(MathF.Round(top * 100, 3)),
                AnimatedValue.Constant(MathF.Round(right * 100, 3)),
                AnimatedValue.Constant(MathF.Round(bottom * 100, 3))),
            Transform = (clip.Transform ?? Transform.Identity) with
            {
                Scale = AnimatedValue.Constant(new ParamValue.Float2(new Vector2(MathF.Round(scale, 5)))),
                Position = AnimatedValue.Constant(new ParamValue.Float2(new Vector2(MathF.Round(shift.X, 2), MathF.Round(shift.Y, 2)))),
            },
        };

        return project.ReplaceTrack(found.Track.ReplaceClip(cropped));
    }
}

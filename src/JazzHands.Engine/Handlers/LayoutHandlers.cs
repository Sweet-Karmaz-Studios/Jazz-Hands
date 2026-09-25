using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Render.Compositing;

namespace JazzHands.Engine.Handlers;

/// <summary>The layouts, their clip counts and what they look like.</summary>
internal static class Layouts
{
    public static readonly LayoutInfo[] All =
    [
        new("facecam", 2, "The first clip fills the frame; the second sits in a corner with rounded corners, a border and a shadow."),
        new("side-by-side", 2, "Two halves, left and right, each filling its half."),
        new("before-after", 2, "Both fill the frame and the second is cut from the left at the split: keyframe its crop.left to wipe between them."),
        new("grid-2", 2, "Two rows, top and bottom: for a vertical frame."),
        new("grid-3", 3, "One large on the left, two small stacked on the right."),
        new("grid-4", 4, "Two by two."),
    ];
}

/// <summary>Lays clips out on the frame.</summary>
public sealed class ApplyLayoutHandler : ICommandHandler<ApplyLayoutCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ApplyLayoutCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        LayoutInfo layout = Layouts.All.FirstOrDefault(known => string.Equals(known.Name, command.Layout, StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandException("unknown-layout", $"There is no layout '{command.Layout}'. There are {string.Join(", ", Layouts.All.Select(known => known.Name))}.");
        string[] ids = command.ClipIds ?? [];
        if (ids.Length != layout.Clips || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new CommandException("invalid-value", $"{layout.Name} lays out {layout.Clips} different clips; {ids.Length} were given.", "clips");
        }

        if (command.Size is <= 0 or > 1 || command.Split is < 0 or > 1 || command.Gap < 0 || command.Margin < 0)
        {
            throw new CommandException("invalid-value", "The size is over 0 and up to 1, the split 0 to 1, and the gap and margin not negative.");
        }

        ClipLocation[] clips = [.. ids.Select(id => HandlerHelp.Clip(project, id))];
        foreach (ClipLocation found in clips)
        {
            HandlerHelp.RequireUnlocked(found.Track);
            if (found.Track.Kind != TrackKind.Video)
            {
                throw new CommandException("not-picture", $"'{found.Clip.Name}' is not a picture on a video track.", "clips");
            }
        }

        ProjectSettings settings = project.SettingsFor(clips[0].Sequence);
        var frame = new Vector2(settings.Width, settings.Height);
        float gap = (float)command.Gap;
        float half = gap / 2;
        (Vector2 Position, Vector2 Size, bool Fill)[] cells = layout.Name switch
        {
            "facecam" => [(Vector2.Zero, frame, true), Corner(project, clips[1].Clip, frame, command)],
            "side-by-side" => [(Vector2.Zero, new(frame.X / 2 - half, frame.Y), true), (new(frame.X / 2 + half, 0), new(frame.X / 2 - half, frame.Y), true)],
            "before-after" => [(Vector2.Zero, frame, true), (Vector2.Zero, frame, true)],
            "grid-2" => [(Vector2.Zero, new(frame.X, frame.Y / 2 - half), true), (new(0, frame.Y / 2 + half), new(frame.X, frame.Y / 2 - half), true)],
            "grid-3" =>
            [
                (Vector2.Zero, new(frame.X * 2 / 3 - half, frame.Y), true),
                (new(frame.X * 2 / 3 + half, 0), new(frame.X / 3 - half, frame.Y / 2 - half), true),
                (new(frame.X * 2 / 3 + half, frame.Y / 2 + half), new(frame.X / 3 - half, frame.Y / 2 - half), true),
            ],
            _ =>
            [
                (Vector2.Zero, new(frame.X / 2 - half, frame.Y / 2 - half), true),
                (new(frame.X / 2 + half, 0), new(frame.X / 2 - half, frame.Y / 2 - half), true),
                (new(0, frame.Y / 2 + half), new(frame.X / 2 - half, frame.Y / 2 - half), true),
                (new(frame.X / 2 + half, frame.Y / 2 + half), new(frame.X / 2 - half, frame.Y / 2 - half), true),
            ],
        };

        for (int index = 0; index < clips.Length; index++)
        {
            ClipLocation found = project.FindClip(clips[index].Clip.Id)!;
            Clip placed = Place(project, found.Clip, frame, cells[index]);
            if (layout.Name == "before-after" && index == 1)
            {
                placed = placed with { Crop = placed.Crop! with { Left = AnimatedValue.Constant((float)(command.Split * 100)) } };
            }

            if (layout.Name == "facecam" && index == 1 && !placed.Effects.Any(effect => effect.TypeId == "video.frame"))
            {
                Effect frameEffect = Effect.Create("video.frame");
                placed = placed with { Effects = [.. placed.Effects, frameEffect] };
                context.Changed(frameEffect.Id);
            }

            context.Changed(placed.Id);
            project = project.ReplaceTrack(found.Track.ReplaceClip(placed));
        }

        return project;
    }

    /// <summary>The facecam's cell: its width a fraction of the frame's, its height by its own shape, in the corner.</summary>
    private static (Vector2, Vector2, bool) Corner(Project project, Clip clip, Vector2 frame, ApplyLayoutCommand command)
    {
        Vector2 fitted = Fitted(project, clip, frame);
        float width = frame.X * (float)command.Size;
        var size = new Vector2(width, width * fitted.Y / fitted.X);
        float margin = (float)command.Margin;
        float x = command.Corner.EndsWith("left", StringComparison.OrdinalIgnoreCase) ? margin : frame.X - margin - size.X;
        float y = command.Corner.StartsWith("top", StringComparison.OrdinalIgnoreCase) ? margin : frame.Y - margin - size.Y;
        if (command.Corner is not ("top-left" or "top-right" or "bottom-left" or "bottom-right"))
        {
            throw new CommandException("invalid-value", "The corner is top-left, top-right, bottom-left or bottom-right.", "corner");
        }

        return (new Vector2(x, y), size, false);
    }

    /// <summary>
    /// A clip placed to cover its cell (scaled until both sides reach it and the rest cropped
    /// evenly) or to fit inside it, from its picture as the frame fits it.
    /// </summary>
    private static Clip Place(Project project, Clip clip, Vector2 frame, (Vector2 Position, Vector2 Size, bool Fill) cell)
    {
        Vector2 fitted = Fitted(project, clip, frame);
        Vector2 ratio = cell.Size / fitted;
        float scale = cell.Fill ? MathF.Max(ratio.X, ratio.Y) : MathF.Min(ratio.X, ratio.Y);
        Vector2 kept = Vector2.Min(Vector2.One, cell.Size / (fitted * scale));
        Vector2 cut = (Vector2.One - kept) / 2 * 100;
        Vector2 centre = cell.Position + (cell.Size / 2) - (frame / 2);

        return clip with
        {
            Transform = new Transform(
                AnimatedValue.Constant(new ParamValue.Float2(Round(centre))),
                AnimatedValue.Constant(new ParamValue.Float2(new Vector2(MathF.Round(scale, 5)))),
                AnimatedValue.Constant(0.0f),
                AnimatedValue.Constant(new ParamValue.Float2(Vector2.Zero))),
            Crop = new Crop(
                AnimatedValue.Constant(MathF.Round(cut.X, 4)),
                AnimatedValue.Constant(MathF.Round(cut.Y, 4)),
                AnimatedValue.Constant(MathF.Round(cut.X, 4)),
                AnimatedValue.Constant(MathF.Round(cut.Y, 4))),
        };
    }

    /// <summary>The clip's picture as the frame fits it, before its own transform: the frame for a generator.</summary>
    private static Vector2 Fitted(Project project, Clip clip, Vector2 frame)
    {
        Matrix3x2 placement = RenderGraphBuilder.SourcePlacement(project, clip with { Transform = Transform.Identity }, default, frame);
        Vector2 size = clip.MediaId is { } mediaId
            && project.MediaItem(mediaId)?.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex) is { Width: > 0, Height: > 0 } stream
                ? new Vector2(stream.Width, stream.Height)
                : frame;
        Vector2 fitted = Vector2.Abs(Vector2.TransformNormal(size, placement));
        return fitted.X > 0 && fitted.Y > 0 ? fitted : frame;
    }

    private static Vector2 Round(Vector2 value) => new(MathF.Round(value.X, 2), MathF.Round(value.Y, 2));
}

/// <summary>Lists the layouts.</summary>
public sealed class ListLayoutsHandler : IQueryHandler<ListLayoutsQuery, LayoutInfo[]>
{
    /// <inheritdoc />
    public LayoutInfo[] Handle(Project project, ListLayoutsQuery query, QueryContext context) => Layouts.All;
}

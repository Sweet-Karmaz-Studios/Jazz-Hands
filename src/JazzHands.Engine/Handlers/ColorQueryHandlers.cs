using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Frames;
using JazzHands.Render.Scopes;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>What the colour queries share: a frame rendered for looking at.</summary>
internal static class StillHelp
{
    /// <summary>The sequence asked for, or the active one.</summary>
    internal static Sequence Sequence(Project project, string? sequenceId) =>
        (sequenceId is null ? project.ActiveSequence : project.Sequence(sequenceId))
        ?? throw new CommandException("sequence-not-found", sequenceId is null ? "The project has no sequence to look at." : $"No sequence with id '{sequenceId}'.");

    /// <summary>A frame of a sequence, from the session's still renderer.</summary>
    internal static StillFrame Render(Project project, Sequence sequence, Core.Time.Flicks at, QueryContext context)
    {
        if (at < Core.Time.Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "A frame is looked at from the start of the timeline on; the time is before it.");
        }

        StillRenderer renderer = context.Services?.GetService<StillRenderer>()
            ?? throw new CommandException("no-renderer", "This session has no renderer to look at frames with.");

        return renderer.Render(project, sequence, at, context.Session?.ProjectPath ?? string.Empty);
    }
}

/// <summary>Reads the picture's colour at a point.</summary>
public sealed class SampleColorHandler : IQueryHandler<SampleColorQuery, ColorSample>
{
    /// <inheritdoc />
    public ColorSample Handle(Project project, SampleColorQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        if (query.Size is < 1 or > 64)
        {
            throw new CommandException("value-out-of-range", $"The square averaged is 1 to 64 pixels across; {query.Size} is not.");
        }

        if (query.BeforeEffectId is { } effectId)
        {
            project = Before(project, effectId);
        }

        Sequence sequence = StillHelp.Sequence(project, query.SequenceId);
        StillFrame frame = StillHelp.Render(project, sequence, query.At, context);

        int centreX = (int)Math.Round((frame.Width / 2.0) + query.X);
        int centreY = (int)Math.Round((frame.Height / 2.0) + query.Y);
        if (centreX < 0 || centreY < 0 || centreX >= frame.Width || centreY >= frame.Height)
        {
            throw new CommandException(
                "point-outside-frame",
                $"({query.X}, {query.Y}) from the centre is outside the {frame.Width}x{frame.Height} frame.");
        }

        // The average of premultiplied light, then straightened: a soft edge in the square counts
        // for as much as it covers.
        var sum = Vector4.Zero;
        int half = query.Size / 2;
        int count = 0;
        for (int y = Math.Max(0, centreY - half); y <= Math.Min(frame.Height - 1, centreY - half + query.Size - 1); y++)
        {
            for (int x = Math.Max(0, centreX - half); x <= Math.Min(frame.Width - 1, centreX - half + query.Size - 1); x++)
            {
                int at = ((y * frame.Width) + x) * 4;
                sum += new Vector4(frame.Linear[at], frame.Linear[at + 1], frame.Linear[at + 2], frame.Linear[at + 3]);
                count++;
            }
        }

        Vector4 mean = sum / count;
        Vector3 straight = mean.W > 1e-6f ? new Vector3(mean.X, mean.Y, mean.Z) / mean.W : Vector3.Zero;
        return new ColorSample(
            Math.Round(straight.X, 5),
            Math.Round(straight.Y, 5),
            Math.Round(straight.Z, 5),
            ParamValues.FormatColor(new Vector4(straight, 1.0f)),
            centreX,
            centreY);
    }

    /// <summary>The project with an effect, and every effect after it in its chain, switched off.</summary>
    private static Project Before(Project project, string effectId)
    {
        ParamOwner owner = ParamHelp.Effect(project, effectId);

        EquatableArray<Effect> Off(EquatableArray<Effect> effects)
        {
            int index = effects.Select((effect, position) => (effect, position)).First(pair => pair.effect.Id == effectId).position;
            return new EquatableArray<Effect>([.. effects.Select((effect, position) => position >= index ? effect with { Enabled = false } : effect)]);
        }

        return owner.Clip is { } clip
            ? project.ReplaceTrack(owner.Track.ReplaceClip(clip with { Effects = Off(clip.Effects) }))
            : project.ReplaceTrack(owner.Track with { Effects = Off(owner.Track.Effects) });
    }
}

/// <summary>Measures a frame's histograms and clipping.</summary>
public sealed class MeasureScopesHandler : IQueryHandler<MeasureScopesQuery, ScopeSummary>
{
    /// <inheritdoc />
    public ScopeSummary Handle(Project project, MeasureScopesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = StillHelp.Sequence(project, query.SequenceId);
        ScopeReading scopes = StillHelp.Render(project, sequence, query.At, context).Scopes;

        return new ScopeSummary(
            scopes.FrameWidth,
            scopes.FrameHeight,
            scopes.SampledPixels,
            scopes.Red,
            scopes.Green,
            scopes.Blue,
            scopes.Luma,
            Math.Round(scopes.ClippedHigh, 5),
            Math.Round(scopes.ClippedLow, 5),
            Percentile(scopes.Luma, 0.01),
            Percentile(scopes.Luma, 0.99),
            Math.Round(scopes.Luma.Select((count, value) => (double)count * value).Sum() / Math.Max(1, scopes.SampledPixels), 2));
    }

    private static int Percentile(int[] histogram, double fraction)
    {
        long total = histogram.Sum(count => (long)count);
        long target = (long)Math.Ceiling(total * fraction);
        long running = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            running += histogram[value];
            if (running >= Math.Max(1, target))
            {
                return value;
            }
        }

        return histogram.Length - 1;
    }
}

using JazzHands.Core.Time;
using JazzHands.Media.Decode;

namespace JazzHands.Media.SmartCut;

/// <summary>
/// Where a smart cut copies and where it encodes again: for each stretch, the frames from the cut
/// to the next keyframe encoded, the groups of pictures after it copied, and the frames from the
/// last keyframe to the cut encoded.
/// </summary>
/// <remarks>
/// <para>
/// A copy stops reading at a keyframe in decode order, so in an open group of pictures (HEVC's
/// CRA) it loses that keyframe's leading pictures, which are shown before it but decoded after.
/// The encoded tail therefore starts at the first of those, <see cref="KeyframeIndex.LeadingStart"/>,
/// not at the keyframe: decoding from the keyframe before gives them whole. A copy starting at a
/// keyframe drops that keyframe's own leading pictures for the same reason, and the encoded head
/// before it is what shows them.
/// </para>
/// <para>
/// A stretch that does not reach from one keyframe past the next is encoded whole. A stretch that
/// runs to the end of the file copies to the end. Every time is on the frame grid.
/// </para>
/// </remarks>
public static class SmartSegments
{
    /// <summary>The pieces for some stretches of a source, in order.</summary>
    /// <param name="index">The source's keyframes.</param>
    /// <param name="ranges">The stretches, in source time, on frames.</param>
    /// <param name="rate">The source's frame rate.</param>
    /// <param name="fileEnd">Where the source's picture ends.</param>
    public static IReadOnlyList<SmartSegment> For(KeyframeIndex index, IReadOnlyList<TimeRange> ranges, Rational rate, Flicks fileEnd)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(ranges);

        Flicks OnFrame(Flicks time) => Flicks.FromFrames(time.ToFrames(rate, RoundingMode.Nearest), rate);
        Flicks slack = Flicks.FromFrames(1, rate) / 2;
        Flicks end = OnFrame(fileEnd);
        var segments = new List<SmartSegment>();

        foreach (TimeRange range in ranges)
        {
            Flicks cutIn = OnFrame(range.Start);
            Flicks cutOut = OnFrame(range.End);
            if (cutOut <= cutIn)
            {
                continue;
            }

            bool toTheEnd = cutOut >= end;
            Flicks before = OnFrame(index.AtOrBefore(cutIn + slack));
            Flicks first = before == cutIn ? cutIn : index.After(cutIn + slack) is { } next ? OnFrame(next) : end;

            Flicks last;
            Flicks tailStart;
            if (toTheEnd)
            {
                last = end;
                tailStart = end;
                cutOut = end;
            }
            else
            {
                Flicks raw = index.AtOrBefore(cutOut + slack);
                last = OnFrame(raw);
                tailStart = OnFrame(index.LeadingStart(raw));
                if (tailStart > cutOut)
                {
                    tailStart = cutOut;
                }
            }

            if (first >= tailStart || first >= cutOut)
            {
                // Nothing whole to copy: the stretch sits inside one or two groups of pictures.
                segments.Add(new SmartSegment(cutIn, cutOut, Encode: true, From: before));
                continue;
            }

            if (cutIn < first)
            {
                segments.Add(new SmartSegment(cutIn, first, Encode: true, From: before));
            }

            segments.Add(new SmartSegment(first, tailStart, Encode: false, From: toTheEnd ? null : last));

            if (!toTheEnd && tailStart < cutOut)
            {
                segments.Add(new SmartSegment(tailStart, cutOut, Encode: true, From: OnFrame(index.AtOrBefore(tailStart - slack))));
            }
        }

        return segments;
    }

    /// <summary>The average number of frames between keyframes, for the matched encoder's group length.</summary>
    public static int GopFrames(KeyframeIndex index, Rational rate)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.Count < 2)
        {
            return Math.Max(1, (int)Math.Round(rate.ToDouble() * 2));
        }

        double span = (index.Times[^1] - index.Times[0]).ToSeconds();
        return Math.Max(1, (int)Math.Round(span * rate.ToDouble() / (index.Count - 1)));
    }
}

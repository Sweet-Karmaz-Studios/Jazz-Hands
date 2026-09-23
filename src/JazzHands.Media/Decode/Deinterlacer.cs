using JazzHands.Core.Time;
using JazzHands.Media.Filters;

namespace JazzHands.Media.Decode;

/// <summary>How many frames a deinterlacer makes from each interlaced one.</summary>
public enum DeinterlaceMode
{
    /// <summary>
    /// One progressive frame per interlaced frame, keeping the frame rate. 576i25 becomes 576p25.
    /// </summary>
    Frame,

    /// <summary>
    /// One progressive frame per field, doubling the frame rate. 576i25 becomes 576p50, which is
    /// what the footage actually captured.
    /// </summary>
    Field,
}

/// <summary>
/// Turns interlaced frames into progressive ones with <c>bwdif</c>, so the rest of the engine
/// never sees a field.
/// </summary>
/// <remarks>
/// Everything downstream of the decoder assumes whole progressive frames: the compositor samples
/// a texture, effects run per pixel, and scaling a field pair produces combing that no amount of
/// later filtering removes. Deinterlacing at the source is the only place this can be done once
/// and correctly.
///
/// <c>bwdif</c> needs frames in system memory, so a source that is deinterlaced is decoded in
/// software. See <see cref="Conform.NeedsSoftwareDecode"/>.
/// </remarks>
public sealed class Deinterlacer : IVideoSource
{
    private readonly IVideoSource _source;
    private readonly VideoFilterGraph _graph;
    private readonly bool _ownsSource;
    private bool _sourceEnded;
    private bool _disposed;

    /// <summary>Wraps a source so its frames come out progressive.</summary>
    /// <param name="source">The source to deinterlace.</param>
    /// <param name="frameRate">The source frame rate.</param>
    /// <param name="mode">Whether to keep the frame rate or double it. Default keeps it.</param>
    /// <param name="ownsSource">True to dispose the source with this stage. Default true.</param>
    public Deinterlacer(
        IVideoSource source,
        Rational frameRate,
        DeinterlaceMode mode = DeinterlaceMode.Frame,
        bool ownsSource = true)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _ownsSource = ownsSource;
        Mode = mode;

        // parity=auto reads the field order from each frame, which is what makes a file with
        // mixed or mislabelled field order come out the right way up. deint=all rather than
        // deint=interlaced because this stage is only built for media already known to be
        // interlaced, and plenty of such files forget to set the flag on every frame.
        string filter = mode == DeinterlaceMode.Field
            ? "bwdif=mode=send_field:parity=auto:deint=all"
            : "bwdif=mode=send_frame:parity=auto:deint=all";

        _graph = new VideoFilterGraph(filter, frameRate);
    }

    /// <summary>Whether this stage keeps the frame rate or doubles it.</summary>
    public DeinterlaceMode Mode { get; }

    /// <summary>The filter chain being run, for logs and diagnostics.</summary>
    public string Filter => _graph.Description;

    /// <inheritdoc />
    public VideoFrame? ReadFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            VideoFrame? filtered = _graph.Receive();
            if (filtered is not null)
            {
                return filtered;
            }

            if (_sourceEnded)
            {
                return null;
            }

            using VideoFrame? input = _source.ReadFrame();
            if (input is null)
            {
                _sourceEnded = true;
                _graph.SignalEndOfInput();
                continue;
            }

            _graph.Send(input);
        }
    }

    /// <inheritdoc />
    public void Flush(Flicks resumeAt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _source.Flush(resumeAt);
        _graph.Reset();
        _sourceEnded = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _graph.Dispose();

        if (_ownsSource)
        {
            _source.Dispose();
        }
    }
}

using JazzHands.Core.Time;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>
/// Puts a variable frame rate source onto a fixed grid, holding frames that arrive late and
/// dropping ones that arrive two to a slot.
/// </summary>
/// <remarks>
/// Phone and screen capture recordings present frames whenever something changed, so a clip of
/// them has no frame rate to trim or key against. This stage gives the rest of the engine the
/// frame rate it expects: output frame n is always at exactly n/rate, and its pixels are whatever
/// source frame was nearest that instant.
///
/// No pixels move. A held frame is emitted again as a reference over the same texture slice, so
/// conforming a hardware decoded stream costs one AVFrame shell per output frame and nothing else.
/// That is why this is managed code rather than an <c>fps</c> filter, which would need the frame
/// in system memory.
/// </remarks>
public sealed class FrameRateConformer : IVideoSource
{
    private readonly ILogger _log = Log.ForContext<FrameRateConformer>();
    private readonly IVideoSource _source;
    private readonly Rational _rate;
    private readonly Flicks _frameDuration;
    private readonly FramePool _pool;
    private readonly bool _ownsSource;

    private VideoFrame? _current;
    private VideoFrame? _ahead;
    private bool _currentEmitted;
    private bool _primed;
    private bool _sourceEnded;
    private bool _disposed;
    private long _nextOutput;

    /// <summary>Wraps a source so its frames come out on the grid of <paramref name="rate"/>.</summary>
    /// <param name="source">The source to conform.</param>
    /// <param name="rate">The output frame rate, normally the project's.</param>
    /// <param name="ownsSource">True to dispose the source with this stage. Default true.</param>
    public FrameRateConformer(IVideoSource source, Rational rate, bool ownsSource = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (rate.IsZero || rate.Num < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate.ToString(), "The frame rate must be positive.");
        }

        _source = source;
        _rate = rate;
        _ownsSource = ownsSource;
        _frameDuration = Flicks.FromFrames(1, rate);

        // Two source frames in hand plus the one the caller is holding, with room to spare.
        _pool = new FramePool(6);
    }

    /// <summary>The grid frames come out on.</summary>
    public Rational Rate => _rate;

    /// <summary>Output frames that repeated the previous source frame because none had arrived.</summary>
    public long Duplicated { get; private set; }

    /// <summary>Source frames never shown because a later one landed in the same slot.</summary>
    public long Dropped { get; private set; }

    /// <summary>The index of the next output frame.</summary>
    public long NextFrame => _nextOutput;

    /// <inheritdoc />
    public VideoFrame? ReadFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Prime();

        Flicks time = Flicks.FromFrames(_nextOutput, _rate);

        // Advance while the frame after the current one sits nearer to this slot. Two source
        // frames closest to the same slot means the earlier one is never shown; no source frame
        // near a slot at all means the current one is shown twice. A tie keeps what is already on
        // screen, which is what a player does with a frame that never arrived.
        while (_current is not null && _ahead is not null && Distance(_ahead.Pts, time) < Distance(_current.Pts, time))
        {
            Advance();
        }

        if (_current is null)
        {
            return null;
        }

        if (_sourceEnded && _ahead is null && time >= _current.Pts + _current.Duration)
        {
            // Past the end of the last source frame. Stop rather than repeating it forever.
            return null;
        }

        if (_currentEmitted)
        {
            Duplicated++;
        }

        _currentEmitted = true;
        _nextOutput++;
        return _current.Reference(_pool, time, _frameDuration);
    }

    /// <inheritdoc />
    public void Flush(Flicks resumeAt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _source.Flush(resumeAt);

        Release();
        _primed = false;
        _sourceEnded = false;
        _currentEmitted = false;
        _nextOutput = resumeAt.ToFrames(_rate, RoundingMode.Nearest);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
        _pool.Dispose();

        if (_ownsSource)
        {
            _source.Dispose();
        }

        if (Duplicated > 0 || Dropped > 0)
        {
            _log.Debug(
                "Conformed to {Rate}: {Duplicated} frames held, {Dropped} dropped",
                _rate,
                Duplicated,
                Dropped);
        }
    }

    /// <summary>How far a source frame sits from an output slot, in either direction.</summary>
    private static Flicks Distance(Flicks from, Flicks to) => (from - to).Abs;

    private void Prime()
    {
        if (_primed)
        {
            return;
        }

        _primed = true;
        _current = Pull();
        _currentEmitted = false;
        _ahead = Pull();
    }

    private void Advance()
    {
        if (_current is not null)
        {
            if (!_currentEmitted)
            {
                Dropped++;
            }

            _current.Dispose();
        }

        _current = _ahead;
        _currentEmitted = false;
        _ahead = Pull();
    }

    private VideoFrame? Pull()
    {
        if (_sourceEnded)
        {
            return null;
        }

        VideoFrame? frame = _source.ReadFrame();
        if (frame is null)
        {
            _sourceEnded = true;
        }

        return frame;
    }

    private void Release()
    {
        _current?.Dispose();
        _current = null;
        _ahead?.Dispose();
        _ahead = null;
    }
}

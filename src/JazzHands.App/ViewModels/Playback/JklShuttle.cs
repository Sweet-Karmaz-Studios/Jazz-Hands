using JazzHands.Core.Commands;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>The three shuttle keys.</summary>
public enum ShuttleKey
{
    /// <summary>Backwards, faster with each press.</summary>
    J,

    /// <summary>Stop; held, it turns J and L into single frame steps.</summary>
    K,

    /// <summary>Forwards, faster with each press.</summary>
    L,
}

/// <summary>
/// The J, K and L keys, as every editor since the Avid has had them.
/// </summary>
/// <remarks>
/// L plays forwards and each further press doubles the rate, up to 32 times normal speed. J does
/// the same backwards; pressing J while going forwards turns round at normal speed, and so does L
/// going backwards. K stops. Holding K turns J and L into a single frame step back or forward,
/// which is how a cut point is found by feel.
///
/// The result of each press is a command, the same <c>playback.shuttle</c> or
/// <c>playback.step</c> the CLI sends, so what the keys do can be tested by what they produce and
/// replayed by anything that can send commands. The rate is remembered here rather than read back
/// from the engine, because two quick presses of L must make 2 even when the engine has not yet
/// said it heard the first; <see cref="Sync"/> brings it back into line when something else
/// changes playback.
/// </remarks>
public sealed class JklShuttle
{
    /// <summary>The fastest either way.</summary>
    public const double MaxRate = 32.0;

    private double _rate;
    private bool _holdingK;

    /// <summary>The rate the keys last asked for. Zero when stopped.</summary>
    public double Rate => _rate;

    /// <summary>True while K is held down.</summary>
    public bool IsHoldingK => _holdingK;

    /// <summary>A key went down. Returns the command it means.</summary>
    public ICommand Press(ShuttleKey key)
    {
        switch (key)
        {
            case ShuttleKey.K:
                _holdingK = true;
                _rate = 0;
                return new PauseCommand();

            case ShuttleKey.L when _holdingK:
                return new StepCommand(1);

            case ShuttleKey.J when _holdingK:
                return new StepCommand(-1);

            case ShuttleKey.L:
                _rate = _rate <= 0 ? 1.0 : Math.Min(_rate * 2.0, MaxRate);

                // Normal speed is a play, which also starts again from the top when sitting on
                // the last frame.
                return _rate == 1.0 ? new PlayCommand() : new ShuttleCommand(_rate);

            case ShuttleKey.J:
                _rate = _rate >= 0 ? -1.0 : Math.Max(_rate * 2.0, -MaxRate);
                return new ShuttleCommand(_rate);

            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Not a shuttle key.");
        }
    }

    /// <summary>A key came up. Only K's release matters.</summary>
    public void Release(ShuttleKey key)
    {
        if (key == ShuttleKey.K)
        {
            _holdingK = false;
        }
    }

    /// <summary>Takes the rate from the engine, after something other than these keys changed it.</summary>
    public void Sync(bool playing, double rate) => _rate = playing ? rate : 0;
}

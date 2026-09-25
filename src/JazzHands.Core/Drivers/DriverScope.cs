using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Drivers;

/// <summary>A band of the sound a driver can follow.</summary>
public enum AudioBand
{
    /// <summary>The whole sound.</summary>
    Level,

    /// <summary>Below about 200 Hz: the kick and the bass.</summary>
    Low,

    /// <summary>About 200 Hz to 2 kHz: voices and most instruments.</summary>
    Mid,

    /// <summary>Above about 2 kHz: hats, cymbals and air.</summary>
    High,
}

/// <summary>What a driver reads beyond its own time: the project, the sequence and its sound.</summary>
public interface IDriverEnvironment
{
    /// <summary>The frame rate <c>frame</c> counts in.</summary>
    Rational FrameRate { get; }

    /// <summary>
    /// A track's sound at a moment of the sequence, from 0 (silence) to 1 (its loudest), following
    /// with the attack and release in seconds. The track is named or given by id.
    /// </summary>
    double Audio(string track, AudioBand band, double sequenceSeconds, double attack, double release);

    /// <summary>Another parameter's value at a moment of the sequence; zero when it is not there.</summary>
    DriverValue Param(string owner, string name, Flicks sequenceTime);

    /// <summary>
    /// Seconds from the last marker of a name at or before a moment; before the first, negative,
    /// the time to it; zero when there is no such marker.
    /// </summary>
    double SinceMarker(string name, Flicks sequenceTime);
}

/// <summary>
/// The environment a thread's drivers read, and where the owner being drawn starts on the
/// sequence: set by whatever renders (the render graph builder) around the work.
/// </summary>
/// <remarks>
/// Parameters are evaluated in many places that know only the owner's time, so the rest comes
/// from here rather than through every call. With nothing entered a driver still has its time,
/// <c>frame</c> at 30 fps, and its noise; sound, markers and other parameters read zero.
/// </remarks>
public static class DriverScope
{
    [ThreadStatic]
    private static IDriverEnvironment? _environment;

    [ThreadStatic]
    private static Flicks _origin;

    [ThreadStatic]
    private static int _depth;

    /// <summary>The environment on this thread, or null.</summary>
    public static IDriverEnvironment? Environment => _environment;

    /// <summary>Where the owner being evaluated starts on the sequence.</summary>
    public static Flicks Origin
    {
        get => _origin;
        set => _origin = value;
    }

    /// <summary>Makes an environment current until the result is disposed, when the one before comes back.</summary>
    public static Entered Enter(IDriverEnvironment? environment, Flicks origin = default)
    {
        var entered = new Entered(_environment, _origin);
        _environment = environment;
        _origin = origin;
        return entered;
    }

    /// <summary>
    /// Runs a nested evaluation (<c>param()</c>) unless drivers already reach eight deep, which is
    /// a loop of drivers reading each other: then it is zero.
    /// </summary>
    public static bool TryNest()
    {
        if (_depth >= 8)
        {
            return false;
        }

        _depth++;
        return true;
    }

    /// <summary>Ends what <see cref="TryNest"/> began.</summary>
    public static void Unnest() => _depth--;

    /// <summary>What <see cref="Enter"/> restores.</summary>
    public readonly struct Entered(IDriverEnvironment? environment, Flicks origin) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            _environment = environment;
            _origin = origin;
        }
    }
}

/// <summary>
/// The environment for a project and a sequence: other parameters and markers from the model,
/// and sound from whoever can hear it.
/// </summary>
/// <param name="project">The project.</param>
/// <param name="sequence">The sequence being drawn.</param>
/// <param name="audio">A track's sound level, or null when nothing can read sound (it is then zero).</param>
public sealed class ProjectDriverEnvironment(Project project, Sequence sequence, Func<Sequence, string, AudioBand, double, double, double, double>? audio = null) : IDriverEnvironment
{
    /// <inheritdoc />
    public Rational FrameRate { get; } = project.SettingsFor(sequence).FrameRate;

    /// <inheritdoc />
    public double Audio(string track, AudioBand band, double sequenceSeconds, double attack, double release)
    {
        if (audio is null)
        {
            return 0;
        }

        Track? found = sequence.Tracks.FirstOrDefault(candidate => candidate.Id == track)
            ?? sequence.Tracks.FirstOrDefault(candidate => candidate.Kind == TrackKind.Audio && string.Equals(candidate.Name, track, StringComparison.OrdinalIgnoreCase));
        return found is null ? 0 : audio(sequence, found.Id, band, sequenceSeconds, attack, release);
    }

    /// <inheritdoc />
    public DriverValue Param(string owner, string name, Flicks sequenceTime)
    {
        if (ParamTargets.Find(project, owner) is not { } found
            || ParamTargets.Get(found, name) is not { } value
            || value is KeyframedValue { Keyframes.IsEmpty: true }
            || !DriverScope.TryNest())
        {
            return DriverValue.Of(0);
        }

        try
        {
            using DriverScope.Entered scope = DriverScope.Enter(this, found.Origin);
            return DriverEval.ToDriver(Animation.AnimationEvaluator.Evaluate(value, sequenceTime - found.Origin));
        }
        finally
        {
            DriverScope.Unnest();
        }
    }

    /// <inheritdoc />
    public double SinceMarker(string name, Flicks sequenceTime)
    {
        Marker? last = null;
        Marker? first = null;
        foreach (Marker marker in sequence.Markers)
        {
            if (!string.Equals(marker.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (first is null || marker.Time < first.Time)
            {
                first = marker;
            }

            if (marker.Time <= sequenceTime && (last is null || marker.Time > last.Time))
            {
                last = marker;
            }
        }

        return last is not null ? (sequenceTime - last.Time).ToSeconds()
            : first is not null ? (sequenceTime - first.Time).ToSeconds()
            : 0;
    }
}

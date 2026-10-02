using JazzHands.Core.Commands;
using JazzHands.Core.Time;

namespace JazzHands.Engine.Playback;

/// <summary>
/// What the <c>playback.*</c> commands drive.
/// </summary>
/// <remarks>
/// The handlers find one of these among the session's services. A running editor registers its
/// <see cref="PlaybackEngine"/>; a headless <c>jazz</c> process registers nothing, and the handlers
/// say so with <c>no-playback</c> rather than pretending to play.
///
/// It is an interface so the command tests can drive a recording fake and assert on what was
/// asked, without a sound card, a GPU or a thread.
/// </remarks>
public interface IPlaybackController
{
    /// <summary>Where the playhead is: what is being heard while playing, where it was put while not.</summary>
    Flicks Position { get; }

    /// <summary>Plays from the playhead at normal speed.</summary>
    void Play();

    /// <summary>Stops where the playhead is.</summary>
    void Pause();

    /// <summary>Plays when paused and pauses when playing.</summary>
    void Toggle();

    /// <summary>Stops and goes back to where playback last started.</summary>
    void Stop();

    /// <summary>Moves the playhead. Playing carries on from there.</summary>
    void Seek(Flicks time);

    /// <summary>Pauses, then moves the playhead by whole frames on the sequence's grid.</summary>
    void Step(int frames);

    /// <summary>Plays at a rate: 2 is double speed, negative is backwards, 0 pauses.</summary>
    void Shuttle(double rate);

    /// <summary>Loops over the in and out points, or the whole sequence.</summary>
    bool Loop { get; set; }

    /// <summary>How much of the frame the preview renders.</summary>
    PreviewQuality Quality { get; set; }

    /// <summary>A multicam clip drawn as the grid of its angles (Phase 41), or null for the program.</summary>
    string? MulticamGrid { get => null; set { } }

    /// <summary>A comp graph node shown in place of its graph's output (Phase 49), or null for the program.</summary>
    string? CompView { get => null; set { } }

    /// <summary>What playback is doing, for <c>playback.state</c>.</summary>
    PlaybackStateInfo Describe();
}

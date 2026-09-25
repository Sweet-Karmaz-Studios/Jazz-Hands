namespace JazzHands.Core.Commands;

/// <summary>Finds the beats of a music clip and marks them on the sequence.</summary>
/// <remarks>
/// Reads the clip's sound over the stretch it plays, finds the attacks, the tempo and the
/// downbeats (music in four at a steady tempo), and puts a <c>beat</c> marker at every beat, named
/// bar.beat (<c>3.1</c> is the downbeat of bar 3), downbeats flagged. The first marker's note says
/// the tempo and how sure the detection was; <c>audio.beat-analysis</c> answers the same without
/// marking anything. Beat markers the clip already had are replaced. The timeline snaps to beat
/// markers, and <c>edit.cut-to-beats</c> cuts on them.
/// </remarks>
/// <param name="ClipId">The music clip: an audio clip, or a video clip whose file has sound.</param>
/// <param name="Keep">Keep beat markers already there instead of replacing them.</param>
[Command("audio.beats", Description = "Find a music clip's beats and mark them on the sequence")]
public sealed record DetectBeatsCommand(
    [property: Arg(0, "The music clip")] string ClipId,
    [property: Option("keep", "Keep beat markers already there")] bool Keep = false) : ICommand;

/// <summary>What beat detection finds in a clip, without marking anything.</summary>
/// <param name="ClipId">The music clip.</param>
[Query("audio.beat-analysis", Description = "The tempo, confidence and beats of a music clip, without marking them")]
public sealed record AnalyzeBeatsQuery(
    [property: Arg(0, "The music clip")] string ClipId) : IQuery<BeatAnalysisInfo>;

/// <summary>A clip's beats.</summary>
/// <param name="ClipId">The clip.</param>
/// <param name="Bpm">The tempo, in beats a minute.</param>
/// <param name="Confidence">How sure the detection is, 0 to 1.</param>
/// <param name="Beats">Every beat, on the sequence.</param>
public sealed record BeatAnalysisInfo(string ClipId, double Bpm, double Confidence, BeatInfo[] Beats);

/// <summary>One beat on the sequence.</summary>
/// <param name="At">Where, on the sequence.</param>
/// <param name="Bar">Which bar, from 1; 0 before the first downbeat.</param>
/// <param name="Beat">Which beat of the bar, from 1.</param>
/// <param name="Downbeat">True for the first beat of a bar.</param>
public sealed record BeatInfo(Time.Flicks At, int Bar, int Beat, bool Downbeat);

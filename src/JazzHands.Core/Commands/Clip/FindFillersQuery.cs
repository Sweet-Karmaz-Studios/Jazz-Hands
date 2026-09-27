using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What <c>clip.remove-fillers</c> would cut, without cutting it: its dry run.</summary>
/// <param name="ClipId">The clip, or a clip linked to it.</param>
/// <param name="Words">The filler words. Default: um, uh, er, erm, ah, hmm, mm.</param>
/// <param name="Pauses">Shorten pauses longer than this; pauses are left alone when not given.</param>
/// <param name="Keep">How much of a long pause is left.</param>
/// <param name="Fillers">Take out the filler words; off to shorten pauses only.</param>
[Query("clip.find-fillers", Description = "List the filler words and long pauses clip.remove-fillers would cut")]
public sealed record FindFillersQuery(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("words", "Comma-separated filler words. Default: um,uh,er,erm,ah,hmm,mm")] EquatableArray<string> Words = default,
    [property: Option("pauses", "Shorten pauses longer than this, such as 0.8s")] Flicks? Pauses = null,
    [property: Option("keep", "How much of a long pause to keep. Default: 0.25 s")] Flicks? Keep = null,
    [property: Option("fillers", "Take out filler words. Default: on")] bool Fillers = true) : IQuery<SpeechCutInfo[]>;

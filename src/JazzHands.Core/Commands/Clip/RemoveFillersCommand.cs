using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Cuts the filler words and long pauses out of a clip.</summary>
/// <remarks>
/// Every filler word goes with the pause after it; every pause between words longer than
/// <c>--pauses</c> is cut down to <c>--keep</c>. Each stretch closes up as <c>clip.remove-words</c>
/// does, and the whole is one undo. <c>clip.find-fillers</c>, with the same options, lists what
/// would go without changing anything: the dry run.
/// </remarks>
/// <param name="ClipId">The clip, or a clip linked to it.</param>
/// <param name="Words">The filler words. Default: um, uh, er, erm, ah, hmm, mm.</param>
/// <param name="Pauses">Shorten pauses longer than this; pauses are left alone when not given.</param>
/// <param name="Keep">How much of a long pause is left.</param>
/// <param name="Fillers">Take out the filler words; off to shorten pauses only.</param>
[Command("clip.remove-fillers", Description = "Cut filler words and long pauses out of a clip")]
public sealed record RemoveFillersCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("words", "Comma-separated filler words. Default: um,uh,er,erm,ah,hmm,mm")] EquatableArray<string> Words = default,
    [property: Option("pauses", "Shorten pauses longer than this, such as 0.8s")] Flicks? Pauses = null,
    [property: Option("keep", "How much of a long pause to keep. Default: 0.25 s")] Flicks? Keep = null,
    [property: Option("fillers", "Take out filler words. Default: on")] bool Fillers = true) : ICommand;

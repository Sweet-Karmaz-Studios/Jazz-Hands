using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What a query says about one transition.</summary>
/// <param name="Id">The transition identifier.</param>
/// <param name="TypeId">Which transition.</param>
/// <param name="Name">The type's name, or the id when this build does not know the type.</param>
/// <param name="TrackId">The track it is on.</param>
/// <param name="LeftClipId">The outgoing clip.</param>
/// <param name="RightClipId">The incoming clip.</param>
/// <param name="Duration">How long it asks to run.</param>
/// <param name="Alignment">Where it sits on the cut.</param>
/// <param name="Cut">The cut, on the timeline.</param>
/// <param name="Start">Where it starts playing, after it has been fitted to the clips.</param>
/// <param name="End">Where it stops.</param>
/// <param name="Fitted">True when the clips leave it less room than it asks for.</param>
/// <param name="ShortBefore">How much source the outgoing clip lacks after its end; zero when it has enough.</param>
/// <param name="ShortAfter">How much source the incoming clip lacks before its start.</param>
/// <param name="Params">Its parameters.</param>
public sealed record TransitionInfo(
    string Id,
    string TypeId,
    string Name,
    string TrackId,
    string LeftClipId,
    string RightClipId,
    Flicks Duration,
    TransitionAlignment Alignment,
    Flicks Cut,
    Flicks Start,
    Flicks End,
    bool Fitted,
    Flicks ShortBefore,
    Flicks ShortAfter,
    ParamInfo[] Params);

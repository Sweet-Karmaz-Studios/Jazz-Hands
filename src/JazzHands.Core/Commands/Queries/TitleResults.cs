using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>One title preset.</summary>
/// <param name="Name">The name <c>--preset</c> takes.</param>
/// <param name="Label">What the editor calls it.</param>
/// <param name="Description">When to use it.</param>
/// <param name="Text">The text a title from it gets when none is given.</param>
/// <param name="Duration">How long a title from it lasts when nothing says.</param>
/// <param name="AnimationIn">What it comes in with.</param>
/// <param name="AnimationOut">What it goes out with.</param>
/// <param name="Params">The parameters it sets, as command-line text, for a 1080 line frame.</param>
/// <param name="BuiltIn">True for one that ships with the editor.</param>
/// <param name="Source">The file it came from; empty for a built-in.</param>
public sealed record TitlePresetInfo(
    string Name,
    string Label,
    string Description,
    string Text,
    Flicks Duration,
    string AnimationIn,
    string AnimationOut,
    IReadOnlyDictionary<string, string> Params,
    bool BuiltIn,
    string Source);

/// <summary>One font family a title can use.</summary>
/// <param name="Family">The name the <c>font</c> parameter takes.</param>
/// <param name="Source"><c>project</c> for one in the project's fonts folder, <c>system</c> for one installed.</param>
/// <param name="Faces">How many weights and styles it has.</param>
public sealed record FontInfo(string Family, string Source, int Faces);

namespace JazzHands.Core.Commands;

/// <summary>Runs several commands as one step.</summary>
/// <remarks>
/// One undo step, and one <c>ProjectChanged</c> event at the end. This is what a drag on the
/// timeline emits when the mouse comes up, and what <c>jazz apply script.json</c> runs, so that
/// twenty small moves are taken back by one press of ctrl+Z rather than twenty.
///
/// If any command in the batch fails, none of them happened: the project goes back to what it was
/// before the first one, because half an edit is worse than no edit.
///
/// Equality is written out by hand. A record compares an array member by reference, so two
/// batches holding the same commands would otherwise be unequal, and the round-trip tests that
/// keep the four surfaces honest would have nothing to compare.
/// </remarks>
/// <param name="Commands">What to run, in order.</param>
/// <param name="Label">What to call the whole thing in the undo menu.</param>
[Command("batch", Description = "Run several commands as one undo step")]
public sealed record BatchCommand(
    [property: Option("commands", "The commands to run, in order")] ICommand[] Commands,
    [property: Option("label", "What to call it in the undo menu")] string Label = "") : ICommand
{
    /// <inheritdoc />
    public bool Equals(BatchCommand? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(Label, other.Label, StringComparison.Ordinal)
            && Commands.Length == other.Commands.Length
            && Commands.Zip(other.Commands).All(pair => Equals(pair.First, pair.Second));
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Label, StringComparer.Ordinal);

        foreach (ICommand command in Commands)
        {
            hash.Add(command);
        }

        return hash.ToHashCode();
    }
}

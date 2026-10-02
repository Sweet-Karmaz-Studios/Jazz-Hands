using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// What a handler's <see cref="IPreparingHandler{TCommand}.Prepare"/> worked out, with what it
/// worked it out from: the records it read (a clip, a track, a media item). The handler reuses it
/// only when those are still the same when the command's turn comes; when another command changed
/// them meanwhile, it works it out again, so nothing out of date is applied.
/// </summary>
/// <param name="Key">What the work was done from, compared by value.</param>
/// <param name="Value">What it worked out.</param>
internal sealed record PreparedWork(object Key, object? Value)
{
    /// <summary>The prepared value when it was worked out from <paramref name="key"/>; otherwise <paramref name="work"/> done now.</summary>
    internal static T Reuse<T>(HandlerContext context, object key, Func<T> work) =>
        context.Prepared is PreparedWork done && Equals(done.Key, key) && done.Value is T value ? value : work();
}

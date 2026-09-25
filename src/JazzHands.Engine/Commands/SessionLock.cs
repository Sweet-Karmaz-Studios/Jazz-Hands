using System.Text.Json.Nodes;
using JazzHands.Core.Commands;

namespace JazzHands.Engine.Commands;

/// <summary>One issuer holding the session for a batch of their own.</summary>
/// <param name="Owner">Who holds it: <c>rpc:claude</c>, <c>mcp</c>.</param>
/// <param name="Reason">Why, as the people refused are told.</param>
/// <param name="Until">When it lets go by itself.</param>
public sealed record SessionLock(string Owner, string Reason, DateTimeOffset Until);

/// <summary>A command that has run, or been refused, and who asked for it.</summary>
/// <param name="Name">The command's registry name.</param>
/// <param name="Args">Its arguments as JSON-RPC would send them.</param>
/// <param name="Issuer">Who asked.</param>
/// <param name="Result">What came back.</param>
/// <param name="At">When it finished.</param>
/// <param name="Elapsed">How long it took, queue included.</param>
public sealed record CommandCompletedEventArgs(string Name, JsonObject Args, string Issuer, CommandResult Result, DateTimeOffset At, TimeSpan Elapsed);

namespace JazzHands.Engine.Commands;

/// <summary>One of the last commands a session ran, as a crash report lists it.</summary>
/// <param name="At">When it finished.</param>
/// <param name="Name">The command's name.</param>
/// <param name="Arguments">Its arguments as JSON.</param>
/// <param name="Issuer">Who asked: gui, cli, mcp, rpc:...</param>
/// <param name="Ok">True when it worked.</param>
/// <param name="Code">The error code when it did not.</param>
public sealed record RecentCommand(DateTimeOffset At, string Name, string Arguments, string Issuer, bool Ok, string? Code);

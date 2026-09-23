namespace JazzHands.Core.Commands;

/// <summary>Enables or disables a clip.</summary>
/// <remarks>A disabled clip stays on the timeline and keeps its place, but does not render.</remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Enabled">False to disable it.</param>
[Command("clip.set-enabled", Description = "Enable or disable a clip without removing it")]
public sealed record SetClipEnabledCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "false to disable")] bool Enabled) : ICommand;

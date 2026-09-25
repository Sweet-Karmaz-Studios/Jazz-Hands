namespace JazzHands.Core.Commands;

/// <summary>Drives a parameter with an expression, worked out each frame, instead of keyframes.</summary>
/// <remarks>
/// <para>
/// The expression is arithmetic over <c>time</c> (seconds from the owner's start), <c>frame</c>,
/// <c>value</c> (the keyframes or value underneath, kept for when the driver is cleared) and
/// functions: <c>wiggle(freq, amount, seed)</c> for smooth random movement,
/// <c>audio("Music", low)</c> for a track's sound from 0 to its loudest (bands <c>level</c>,
/// <c>low</c>, <c>mid</c>, <c>high</c>, then optional attack and release in seconds),
/// <c>param("id", "name")</c> for another parameter, <c>marker("name")</c> for seconds since a
/// marker, <c>loop(period)</c>, <c>pingpong(period)</c>, <c>noise(x, seed)</c>, and <c>sin cos
/// abs min max clamp lerp smoothstep pow sqrt floor round</c>. Vectors are <c>[x, y]</c>; one
/// number stands for every component.
/// </para>
/// <para>
/// A logo pulsing on the kick: <c>transform.scale</c> = <c>value * (1 + audio("Music", low) *
/// 0.15)</c>. A title that wiggles: <c>transform.position</c> = <c>value + wiggle(2, 12)</c>. A
/// vignette tightening as the music builds: <c>amount</c> = <c>0.3 + audio("Music", level, 0.5,
/// 2) * 0.6</c>. Drivers run on picture parameters; sound parameters are not driven.
/// </para>
/// </remarks>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="Expression">The expression.</param>
[Command("param.set-driver", Description = "Drive a parameter with an expression (time, wiggle, audio, param, marker) instead of keyframes")]
public sealed record SetDriverCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Arg(2, "The expression, for example value + wiggle(2, 12)")] string Expression) : ICommand;

/// <summary>Stops driving a parameter; it goes back to the keyframes or value it had underneath.</summary>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
[Command("param.clear-driver", Description = "Stop driving a parameter, back to its keyframes or value")]
public sealed record ClearDriverCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param) : ICommand;

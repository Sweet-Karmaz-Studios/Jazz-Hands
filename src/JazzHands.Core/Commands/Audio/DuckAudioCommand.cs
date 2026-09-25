using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Ducks one track under another: the music drops while the voice has sound.</summary>
/// <remarks>
/// <para>
/// Puts an <c>audio.ducker</c> effect on the music track keyed by the voice track, or updates the
/// one it has. The music goes down by <c>--depth</c> whenever the voice (after its fader) passes
/// <c>--threshold</c>, stays down for <c>--hold</c> after it falls silent, so the gaps between
/// words do not pump it, and comes back over <c>--release</c>. <c>--off</c> takes the ducking off
/// the music track. The Mixer panel marks a ducked strip with its key.
/// </para>
/// <para>Levels may carry their unit: <c>--depth -12dB</c>. One undo.</para>
/// </remarks>
/// <param name="MusicTrackId">The track to turn down.</param>
/// <param name="VoiceTrackId">The track that turns it down.</param>
/// <param name="Depth">How far down, in dB.</param>
/// <param name="Threshold">How loud the voice must be to duck, in dBFS.</param>
/// <param name="Attack">How quickly it goes down.</param>
/// <param name="Hold">How long it stays down after the voice stops.</param>
/// <param name="Release">How quickly it comes back.</param>
/// <param name="Off">Take the ducking off the music track.</param>
[Command("audio.duck", Description = "Duck the music under a voice: one track drops by a depth while another has sound, holding over the gaps between words")]
public sealed record DuckAudioCommand(
    [property: Option("music", "The track to turn down")] string MusicTrackId,
    [property: Option("voice", "The track that turns it down")] string? VoiceTrackId = null,
    [property: Option("depth", "How far down: -12dB. Default: -12")] double Depth = -12,
    [property: Option("threshold", "How loud the voice must be to duck, in dBFS. Default: -40")] double Threshold = -40,
    [property: Option("attack", "How quickly it goes down: 15ms. Default: 15 ms")] Flicks? Attack = null,
    [property: Option("hold", "How long it stays down after the voice stops. Default: 300 ms")] Flicks? Hold = null,
    [property: Option("release", "How quickly it comes back. Default: 400 ms")] Flicks? Release = null,
    [property: Option("off", "Take the ducking off the music track")] bool Off = false) : ICommand;

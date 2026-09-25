namespace JazzHands.Audio.Output;

/// <summary>
/// What an output calls, on its own real-time thread, whenever it wants more samples.
/// </summary>
public interface IAudioRenderCallback
{
    /// <summary>
    /// Fills the start of a buffer with the next samples to play.
    /// </summary>
    /// <remarks>
    /// Called on the output's thread, which is real-time: no lock, no await, no log, no
    /// allocation. The buffer is at the mix rate with the mix channel count.
    /// </remarks>
    /// <param name="output">Where to write.</param>
    /// <param name="frames">How many samples each plane needs.</param>
    /// <param name="submitted">
    /// How many frames the output had been given before these, counted since it started. The
    /// first of these frames will be heard when <see cref="IAudioOutput.FramesPlayed"/> reaches
    /// this number, which is how a clock anchors a seek to the moment it becomes audible.
    /// </param>
    void Render(AudioBuffer output, int frames, long submitted);
}

/// <summary>
/// Somewhere mixed audio goes to be heard.
/// </summary>
/// <remarks>
/// <see cref="WasapiOutput"/> is the speakers. <see cref="ManualAudioOutput"/> is driven by hand,
/// for tests and for anything headless that wants the transport without a sound card.
/// </remarks>
public interface IAudioOutput : IDisposable
{
    /// <summary>The rate the output is fed at.</summary>
    int SampleRate { get; }

    /// <summary>The channel count the output is fed with.</summary>
    int Channels { get; }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Frames the device has actually played since the output started, which is the master
    /// clock while audio plays. Monotonic, and safe to read from any thread.
    /// </summary>
    long FramesPlayed { get; }

    /// <summary>Times the device ran dry and played a gap. Zero is the whole point.</summary>
    long Underruns { get; }

    /// <summary>A name for the device, for logs and the status bar.</summary>
    string DeviceName { get; }

    /// <summary>
    /// Times the output moved to another device because the one it had went away (unplugged,
    /// disabled) or the default changed. The transport tells the person (Phase 33).
    /// </summary>
    int DeviceSwitches => 0;

    /// <summary>Starts pulling from a callback.</summary>
    void Start(IAudioRenderCallback callback);

    /// <summary>Stops pulling. What was already given to the device may still play out.</summary>
    void Stop();
}

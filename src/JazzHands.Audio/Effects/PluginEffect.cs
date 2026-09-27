using System.Collections.Concurrent;
using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>What a plugin effect asks the engine to start.</summary>
/// <param name="Library">The plugin's file (a .clap).</param>
/// <param name="PluginId">The plugin's id within it.</param>
/// <param name="State">Its saved state; empty for a fresh one.</param>
/// <param name="SampleRate">The mix rate.</param>
/// <param name="Channels">The channels it processes.</param>
/// <param name="Offline">True for an export: the plugin is told it renders offline, and is waited for.</param>
public sealed record PluginRequest(string Library, string PluginId, byte[] State, int SampleRate, int Channels, bool Offline);

/// <summary>
/// A plugin running somewhere the engine started it (a process of its own), as the mixer sees it:
/// planes to fill, a block to run, planes to read. The audio side of it allocates nothing.
/// </summary>
public interface IPluginRunner : IDisposable
{
    /// <summary>Samples the plugin's output lags its input by.</summary>
    int Latency { get; }

    /// <summary>True once it has stopped working; it is bypassed from then on.</summary>
    bool Failed { get; }

    /// <summary>A channel's input plane for the next block.</summary>
    Span<float> Input(int channel, int frames);

    /// <summary>A channel's output plane after <see cref="Run"/>.</summary>
    ReadOnlySpan<float> Output(int channel, int frames);

    /// <summary>Runs a block with parameter changes at sample offsets; false when it failed or did not answer in time.</summary>
    bool Run(int frames, ReadOnlySpan<(uint Id, double Value, int Offset)> changes, int timeoutMilliseconds);

    /// <summary>The plugin's state now, as it saves it.</summary>
    byte[] SaveState();
}

/// <summary>
/// A CLAP plugin as an audio effect (Phase 46). The engine starts the plugin (<see cref="Start"/>,
/// set by the engine: a process of its own); this passes the clip's or track's sound through it a
/// block at a time with the effect's automated parameters, and passes the sound through untouched
/// when there is no plugin (missing, crashed): bypassed, never lost.
/// </summary>
/// <remarks>
/// <para>
/// Which plugin is the effect's <c>plugin</c> and <c>library</c> text; its saved state is
/// <c>state</c> (base64). Its own parameters are stored on the effect as <c>p&lt;id&gt;</c>, plain
/// values that may be keyframed like any other; the effect host hands their curves to
/// <see cref="Automate"/>, and each block the values that changed are sent as CLAP parameter events
/// at the block's first sample.
/// </para>
/// <para>
/// A plugin's latency is compensated as a clip effect's is (the mixer reads ahead), so a latent
/// plugin belongs on a clip. An export is rendered offline: <see cref="AudioEffectHost.Offline"/>.
/// </para>
/// </remarks>
[AudioEffect(TypeId, Name = "Plugin", Category = "Plugins", Description = "A CLAP plugin on this computer, hosted in a process of its own; its parameters are automated like any effect's.")]
[Param("plugin", ParamType.Text, Default = "", Animatable = false, Description = "The plugin's id, from plugin.list.")]
[Param("library", ParamType.Text, Default = "", Animatable = false, Description = "The plugin's file.")]
[Param("state", ParamType.Text, Default = "", Animatable = false, Description = "The plugin's saved state, base64, as plugin.save-state captured it.")]
public sealed class PluginEffect : AudioEffect
{
    /// <summary>The effect's type id.</summary>
    public const string TypeId = "audio.plugin";

    /// <summary>The prefix of a plugin parameter's name on the effect: <c>p</c> and its CLAP id.</summary>
    public const string ParameterPrefix = "p";

    private static readonly ConcurrentDictionary<string, WeakReference<PluginEffect>> Live = new(StringComparer.Ordinal);

    private readonly (uint Id, double Value, int Offset)[] _changes = new (uint, double, int)[256];
    private string _plugin = string.Empty;
    private string _library = string.Empty;
    private string _state = string.Empty;
    private IPluginRunner? _runner;
    private (uint Id, ScalarCurve Curve)[] _automation = [];
    private double[] _sent = [];
    private bool _started;

    /// <summary>Starts a plugin; set by the engine. Null leaves every plugin effect bypassed.</summary>
    public static Func<PluginRequest, IPluginRunner?>? Start { get; set; }

    /// <summary>Told when a plugin could not start or stopped working: the effect's id and why.</summary>
    public static Action<string, string>? Notice { get; set; }

    /// <summary>The effect's id in the project, once its host has placed it.</summary>
    public string EffectId { get; internal set; } = string.Empty;

    /// <summary>True when rendering an export.</summary>
    public bool Offline { get; internal set; }

    /// <inheritdoc />
    public override int LatencySamples => _runner is { Failed: false } runner ? runner.Latency : 0;

    /// <summary>The running plugin effect of a project effect, when a mix holds one.</summary>
    public static PluginEffect? Find(string effectId) =>
        Live.TryGetValue(effectId, out WeakReference<PluginEffect>? reference) && reference.TryGetTarget(out PluginEffect? effect) ? effect : null;

    /// <summary>The plugin's state now, from the running plugin; null when none is running.</summary>
    public byte[]? CaptureState() => _runner is { Failed: false } runner ? runner.SaveState() : null;

    /// <inheritdoc />
    public override void Text(string name, string value)
    {
        switch (name)
        {
            case "plugin" when value != _plugin:
                _plugin = value;
                _started = false;
                break;
            case "library" when value != _library:
                _library = value;
                _started = false;
                break;
            case "state" when value != _state:
                _state = value;
                _started = false;
                break;
        }
    }

    /// <summary>The curves of the plugin's automated parameters. On the building thread.</summary>
    public void Automate(IReadOnlyList<(uint Id, ScalarCurve Curve)> automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = [.. automation];
        _sent = [.. Enumerable.Repeat(double.NaN, _automation.Length)];
        StartIfNeeded();
    }

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        if (_runner is not { Failed: false } runner)
        {
            return;
        }

        int channels = Math.Min(block.Channels, 8);
        for (int channel = 0; channel < channels; channel++)
        {
            block.Plane(channel).CopyTo(runner.Input(channel, block.Frames));
        }

        int count = 0;
        for (int index = 0; index < _automation.Length && count < _changes.Length; index++)
        {
            double value = _automation[index].Curve.Evaluate(block.Time);
            if (value != _sent[index])
            {
                _sent[index] = value;
                _changes[count++] = (_automation[index].Id, value, 0);
            }
        }

        if (!runner.Run(block.Frames, _changes.AsSpan(0, count), Offline ? 10_000 : 100))
        {
            // Bypassed: the block keeps its own sound, and the engine is told once.
            Notice?.Invoke(EffectId, $"The plugin {_plugin} stopped working and is bypassed.");
            return;
        }

        for (int channel = 0; channel < channels; channel++)
        {
            runner.Output(channel, block.Frames).CopyTo(block.Plane(channel));
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        base.Reset();
        Array.Fill(_sent, double.NaN);
    }

    /// <summary>Stops the plugin.</summary>
    public void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        Live.TryRemove(EffectId, out _);
    }

    private void StartIfNeeded()
    {
        if (_started || SampleRate <= 0)
        {
            return;
        }

        _started = true;
        _runner?.Dispose();
        _runner = null;
        if (_plugin.Length == 0 || _library.Length == 0 || Start is not { } start)
        {
            return;
        }

        byte[] state = _state.Length > 0 ? Convert.FromBase64String(_state) : [];
        try
        {
            _runner = start(new PluginRequest(_library, _plugin, state, SampleRate, Channels, Offline));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Notice?.Invoke(EffectId, $"The plugin {_plugin} could not start and is bypassed: {exception.Message}");
        }

        if (EffectId.Length > 0)
        {
            Live[EffectId] = new WeakReference<PluginEffect>(this);
        }
    }
}

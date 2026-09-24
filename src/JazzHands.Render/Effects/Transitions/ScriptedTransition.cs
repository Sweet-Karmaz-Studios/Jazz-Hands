using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Transitions;

/// <summary>
/// A transition written outside the build (<see cref="CustomTransitions"/>): its shader compiled
/// from its file, its parameters packed as the loader says.
/// </summary>
/// <remarks>
/// The compositor makes one per type and asks <see cref="IsStale"/> before each frame, so a saved
/// edit to the file, or a reloaded manifest, makes a fresh one with the new source.
/// </remarks>
public sealed class ScriptedTransition : VideoTransition
{
    private readonly EffectDescriptor _descriptor;
    private readonly string _path;
    private readonly DateTime _written;
    private readonly PassDescriptor _pass;

    /// <summary>Reads the shader of a loaded transition.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public ScriptedTransition(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        _descriptor = descriptor;
        _path = descriptor.SourceFile ?? throw new ArgumentException($"'{descriptor.TypeId}' has no source file.", nameof(descriptor));
        _written = File.GetLastWriteTimeUtc(_path);
        _pass = new PassDescriptor(_path, CustomTransitions.EntryOf(descriptor)) { Source = File.ReadAllText(_path) };
        Passes = [_pass];
    }

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; }

    /// <summary>True when the shader did not compile; it then cuts in the middle.</summary>
    public bool Broken { get; internal set; }

    /// <summary>True when the file has been saved since this was made, or the type was reloaded.</summary>
    public bool IsStale(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        try
        {
            return !ReferenceEquals(descriptor, _descriptor) || File.GetLastWriteTimeUtc(_path) != _written;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, float progress, RenderTarget outgoing, RenderTarget incoming, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(output);

        if (Broken || progress <= 0.0f || progress >= 1.0f)
        {
            context.Copy(progress < 0.5f ? outgoing : incoming, output);
            return;
        }

        TransitionValues values = CustomTransitions.Pack(context, parameters);
        values.Progress = progress;
        values.Seed = ((uint)context.Seed & 0xFFFFFFu) >> 1;
        context.Draw(_pass, output, in values, outgoing, incoming);
    }
}

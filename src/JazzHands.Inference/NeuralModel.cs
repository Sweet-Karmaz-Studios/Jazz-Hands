using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace JazzHands.Inference;

/// <summary>Where a model runs.</summary>
public enum ExecutionProvider
{
    /// <summary>The GPU through DirectML: any DirectX 12 graphics card, nothing else installed.</summary>
    DirectML,

    /// <summary>The processor.</summary>
    Cpu,
}

/// <summary>
/// A neural network loaded for running (Phase 42): ONNX Runtime with the DirectML execution
/// provider when there is a DirectX 12 GPU, and the CPU when not or when asked. The one way the
/// engine runs a model, for background removal, object masks and speech enhancement.
/// </summary>
/// <remarks>
/// <para>
/// DirectML wants memory patterns off and sequential execution; a model it cannot take (an
/// operator it lacks) falls back to the CPU at load, said in the log and in <see cref="Provider"/>.
/// A session is not thread safe for runs that share it, so a caller that runs from several threads
/// takes a model each or locks.
/// </para>
/// <para>
/// Frames reach a model through the processor (a copy down from the GPU and one back up): our
/// frames are Direct3D 11 textures and DirectML takes Direct3D 12 buffers. That copy is the named
/// exception to "frames stay on the GPU", measured in spike S10.
/// </para>
/// </remarks>
public sealed class NeuralModel : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<NeuralModel>();
    private readonly InferenceSession _session;

    private NeuralModel(InferenceSession session, ExecutionProvider provider, string path)
    {
        _session = session;
        Provider = provider;
        Path = path;
    }

    /// <summary>Where the model runs.</summary>
    public ExecutionProvider Provider { get; }

    /// <summary>The model's file.</summary>
    public string Path { get; }

    /// <summary>Its inputs, by name.</summary>
    public IReadOnlyDictionary<string, NodeMetadata> Inputs => _session.InputMetadata;

    /// <summary>Its outputs, by name.</summary>
    public IReadOnlyDictionary<string, NodeMetadata> Outputs => _session.OutputMetadata;

    /// <summary>
    /// Loads a model: on the GPU through DirectML when <paramref name="gpu"/> is true and it loads
    /// there, else on the CPU.
    /// </summary>
    /// <param name="path">The .onnx file.</param>
    /// <param name="gpu">Try the GPU first.</param>
    /// <param name="adapter">Which DirectX adapter, 0 for the first.</param>
    public static NeuralModel Load(string path, bool gpu = true, int adapter = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no model at {path}.", path);
        }

        if (gpu)
        {
            SessionOptions? options = null;
            try
            {
                options = new SessionOptions
                {
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                options.AppendExecutionProvider_DML(adapter);
                var session = new InferenceSession(path, options);
                Log.Information("Loaded {Model} on DirectML adapter {Adapter}", System.IO.Path.GetFileName(path), adapter);
                return new NeuralModel(session, ExecutionProvider.DirectML, path);
            }
            catch (OnnxRuntimeException exception)
            {
                Log.Warning(exception, "{Model} would not load on DirectML; running it on the CPU", System.IO.Path.GetFileName(path));
            }
            finally
            {
                options?.Dispose();
            }
        }

        using var cpu = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new NeuralModel(new InferenceSession(path, cpu), ExecutionProvider.Cpu, path);
    }

    /// <summary>Runs the model on named inputs and returns its outputs, which the caller disposes.</summary>
    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return _session.Run(inputs);
    }

    /// <summary>A float tensor of a shape, filled from <paramref name="values"/> when given, else zeros.</summary>
    public static DenseTensor<float> Tensor(ReadOnlySpan<int> shape, float[]? values = null) =>
        values is null ? new DenseTensor<float>(shape) : new DenseTensor<float>(values, shape);

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Models;
using JazzHands.Inference;
using JazzHands.Media.Analysis;
using JazzHands.Media.Import;
using JazzHands.Render.Compositing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace JazzHands.Engine.Caching;

/// <summary>
/// Person mattes for media files (Phase 43): Robust Video Matting run once over a video stream,
/// frame by frame in order with its recurrent state, and kept in the cache folder, so background
/// removal plays smoothly after the first pass and never runs a network while drawing.
/// </summary>
/// <remarks>
/// <para>
/// A matte file (<c>mattes\&lt;digits&gt;_&lt;stream&gt;_rvm.jmatte</c>) holds one eight bit alpha
/// picture a source frame, each Brotli compressed (a matte is mostly flat, so a 720p one is a few
/// kilobytes), with a table of where each starts. The pictures are at the size the network ran at,
/// the source fitted into 1280x720, and the effect stretches them over the clip's picture.
/// </para>
/// <para>
/// The recurrent state carries what the network learned from the frames before, which is what
/// keeps a matte from flickering; at a cut (the picture's mean brightness change jumps) it starts
/// again, so one shot's person does not bleed into the next.
/// </para>
/// </remarks>
public sealed class MatteService(CacheManager? cache = null)
{
    /// <summary>The model a person matte comes from.</summary>
    public static ModelFile Model { get; } = ModelStore.Find("rvm-mobilenetv3")!;

    private const int Magic = 0x3154_4D4A; // "JMT1"

    /// <summary>How many times a shot's first frame is run before its matte is kept.</summary>
    private const int WarmUp = 4;
    private static readonly ILogger Log = Serilog.Log.ForContext<MatteService>();
    private readonly ConcurrentDictionary<string, MatteFile> _open = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, MatteService> Shared = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The service for a cache: one a folder, shared by the session and every frame server.</summary>
    public static MatteService For(CacheManager? cache) => Shared.GetOrAdd(cache?.Folder ?? CacheManager.DefaultFolder, _ => new MatteService(cache));

    /// <summary>Where mattes are kept: beside the proxies in the cache folder (the default one when none is given).</summary>
    public string Folder => Path.Combine(cache?.Folder ?? CacheManager.DefaultFolder, "mattes");

    /// <summary>The file a stream's matte is kept in.</summary>
    public string PathFor(string hash, int stream) =>
        Path.Combine(Folder, $"{(hash.StartsWith("xxh64:", StringComparison.Ordinal) ? hash[6..] : hash)}_{stream}_rvm.jmatte");

    /// <summary>The matte of a stream, when it has been made; null when not.</summary>
    public MatteFile? Cached(string hash, int stream)
    {
        if (hash.Length == 0)
        {
            return null;
        }

        string path = PathFor(hash, stream);
        if (_open.TryGetValue(path, out MatteFile? known))
        {
            return known;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return _open.GetOrAdd(path, MatteFile.Open);
        }
        catch (InvalidDataException exception)
        {
            Log.Warning(exception, "A matte file was not one: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Makes a stream's matte: every frame through the network, in order. Throws
    /// <see cref="FileNotFoundException"/> when the model is not downloaded.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="path">Where its file is.</param>
    /// <param name="stream">The video stream.</param>
    /// <param name="progress">Told the fraction done.</param>
    /// <param name="cancellationToken">Stops it, leaving nothing behind.</param>
    public MatteFile Analyze(MediaItem item, string path, MediaStream stream, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(stream);
        if (Cached(item.Hash, stream.Index) is { } known)
        {
            progress?.Report(1.0);
            return known;
        }

        if (!ModelStore.IsPresent(Model))
        {
            throw new FileNotFoundException($"The background removal model is not downloaded. `model.download {Model.Name}` fetches it ({Model.Bytes / 1e6:0} MB).", ModelStore.PathOf(Model));
        }

        (int width, int height) = SizeFor(stream.Width > 0 ? stream.Width : 1920, stream.Height > 0 ? stream.Height : 1080);
        Rational rate = stream.FrameRate is { IsZero: false } given ? given : new Rational(30, 1);
        long expected = Math.Max(1, (long)(stream.Duration.ToSeconds() * rate.ToDouble()));
        string target = PathFor(item.Hash, stream.Index);
        Directory.CreateDirectory(Folder);
        string part = target + ".part";

        try
        {
            using (NeuralModel model = NeuralModel.Load(ModelStore.PathOf(Model)))
            using (var reader = new RgbReader(path, width, height))
            using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var offsets = new List<long>();
                var times = new List<long>();
                var body = new MemoryStream();
                DenseTensor<float>[] state = Fresh();
                float downsample = Math.Min(1.0f, 512.0f / Math.Max(width, height));
                float[] lastLuma = [];
                var clock = System.Diagnostics.Stopwatch.StartNew();

                while (reader.ReadNext() is { } picture)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    float[] luma = Thumbnail(picture);
                    // A shot's first frame is heard a few times first: the recurrent state needs
                    // frames to settle, and a matte that sharpens over the first few flickers.
                    bool fresh = lastLuma.Length == 0 || Cut(lastLuma, luma);
                    if (fresh)
                    {
                        state = Fresh();
                        for (int warm = 0; warm < WarmUp; warm++)
                        {
                            (_, state) = Run(model, picture, state, downsample);
                        }
                    }

                    lastLuma = luma;
                    (byte[] alpha, state) = Run(model, picture, state, downsample);
                    offsets.Add(body.Position);
                    times.Add(picture.Time.Value);
                    using (var brotli = new BrotliStream(body, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        brotli.Write(alpha);
                    }

                    progress?.Report(Math.Min(0.999, offsets.Count / (double)expected));
                }

                WriteHeader(file, width, height, rate, offsets, times);
                body.Position = 0;
                body.CopyTo(file);
                Log.Information("Matted {Media}: {Frames} frames at {Width}x{Height} on {Provider} in {Seconds:F1} s", item.Name, offsets.Count, width, height, model.Provider, clock.Elapsed.TotalSeconds);
            }

            File.Move(part, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }
        }

        progress?.Report(1.0);
        _open.TryRemove(target, out _);
        return Cached(item.Hash, stream.Index)!;
    }

    /// <summary>Forgets the open files, after the cache is cleared or a file is replaced.</summary>
    public void Clear() => _open.Clear();

    /// <summary>Deletes the matte files of one file content, or every one when no hash is given (<c>cache.clear --analyses</c>).</summary>
    public void Delete(string? hash = null)
    {
        _open.Clear();
        if (!Directory.Exists(Folder))
        {
            return;
        }

        string pattern = hash is { Length: > 0 } given ? $"{(given.StartsWith("xxh64:", StringComparison.Ordinal) ? given[6..] : given)}_*_rvm.jmatte" : "*.jmatte";
        foreach (string file in Directory.EnumerateFiles(Folder, pattern))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException exception)
            {
                Log.Warning(exception, "Could not delete the matte {Path}", file);
            }
        }
    }


    /// <summary>The size the network runs at: the source fitted into 1280x720, even on both sides.</summary>
    internal static (int Width, int Height) SizeFor(int width, int height)
    {
        double scale = Math.Min(1.0, Math.Min(1280.0 / width, 720.0 / height));
        return (Math.Max(2, (int)Math.Round(width * scale / 2) * 2), Math.Max(2, (int)Math.Round(height * scale / 2) * 2));
    }

    private static DenseTensor<float>[] Fresh() => [.. Enumerable.Range(0, 4).Select(_ => new DenseTensor<float>([1, 1, 1, 1]))];

    /// <summary>One picture through the network with the state from the one before; the matte as bytes and the next state.</summary>
    private static (byte[] Alpha, DenseTensor<float>[] State) Run(NeuralModel model, RgbImage picture, DenseTensor<float>[] state, float downsample)
    {
        int width = picture.Width;
        int height = picture.Height;
        var source = new DenseTensor<float>([1, 3, height, width]);
        Span<float> planes = source.Buffer.Span;
        int area = width * height;
        byte[] rgb = picture.Pixels;
        for (int index = 0; index < area; index++)
        {
            planes[index] = rgb[index * 3] / 255f;
            planes[area + index] = rgb[(index * 3) + 1] / 255f;
            planes[(2 * area) + index] = rgb[(index * 3) + 2] / 255f;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("src", source),
            NamedOnnxValue.CreateFromTensor("r1i", state[0]),
            NamedOnnxValue.CreateFromTensor("r2i", state[1]),
            NamedOnnxValue.CreateFromTensor("r3i", state[2]),
            NamedOnnxValue.CreateFromTensor("r4i", state[3]),
            NamedOnnxValue.CreateFromTensor("downsample_ratio", new DenseTensor<float>(new[] { downsample }, [1])),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = model.Run(inputs);
        Tensor<float> alpha = results.First(value => value.Name == "pha").AsTensor<float>();
        byte[] bytes = new byte[area];
        int at = 0;
        foreach (float value in alpha)
        {
            bytes[at++] = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        }

        DenseTensor<float>[] next = [.. new[] { "r1o", "r2o", "r3o", "r4o" }.Select(name => results.First(value => value.Name == name).AsTensor<float>().ToDenseTensor())];
        return (bytes, next);
    }

    /// <summary>A 32x18 brightness picture of a frame, for noticing cuts.</summary>
    private static float[] Thumbnail(RgbImage picture)
    {
        const int Across = 32;
        const int Down = 18;
        float[] cells = new float[Across * Down];
        int[] counts = new int[Across * Down];
        for (int y = 0; y < picture.Height; y += 2)
        {
            int row = Math.Min(Down - 1, y * Down / picture.Height);
            for (int x = 0; x < picture.Width; x += 2)
            {
                int cell = (row * Across) + Math.Min(Across - 1, x * Across / picture.Width);
                int at = ((y * picture.Width) + x) * 3;
                cells[cell] += (0.2126f * picture.Pixels[at]) + (0.7152f * picture.Pixels[at + 1]) + (0.0722f * picture.Pixels[at + 2]);
                counts[cell]++;
            }
        }

        for (int cell = 0; cell < cells.Length; cell++)
        {
            cells[cell] /= Math.Max(1, counts[cell]);
        }

        return cells;
    }

    /// <summary>True when two thumbnails differ as a cut does: a third of the range on average.</summary>
    private static bool Cut(float[] before, float[] after) =>
        before.Zip(after, (a, b) => MathF.Abs(a - b)).Average() > 255f / 3f;

    private static void WriteHeader(Stream file, int width, int height, Rational rate, List<long> offsets, List<long> times)
    {
        Span<byte> header = stackalloc byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], (int)rate.Num);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], (int)rate.Den);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], offsets.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 0);
        file.Write(header);
        byte[] table = new byte[offsets.Count * 16];
        for (int index = 0; index < offsets.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(index * 16), offsets[index]);
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan((index * 16) + 8), times[index]);
        }

        file.Write(table);
    }

    /// <summary>A matte file opened for reading: its pictures by source time, a few kept unpacked.</summary>
    public sealed class MatteFile
    {
        private readonly string _path;
        private readonly long[] _offsets;
        private readonly long[] _times;
        private readonly long _bodyStart;
        private readonly long _length;
        private readonly Lock _gate = new();
        private readonly LinkedList<(int Index, MatteFrame Frame)> _recent = new();

        private MatteFile(string path, int width, int height, long[] offsets, long[] times, long bodyStart, long length)
        {
            _path = path;
            Width = width;
            Height = height;
            _offsets = offsets;
            _times = times;
            _bodyStart = bodyStart;
            _length = length;
        }

        /// <summary>Pixels across.</summary>
        public int Width { get; }

        /// <summary>Pixels down.</summary>
        public int Height { get; }

        /// <summary>How many pictures it holds.</summary>
        public int Count => _offsets.Length;

        /// <summary>Opens a matte file.</summary>
        public static MatteFile Open(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[28];
            file.ReadExactly(header);
            if (BinaryPrimitives.ReadInt32LittleEndian(header) != Magic)
            {
                throw new InvalidDataException($"{path} is not a matte file.");
            }

            int width = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            int height = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            byte[] table = new byte[count * 16];
            file.ReadExactly(table);
            long[] offsets = new long[count];
            long[] times = new long[count];
            for (int index = 0; index < count; index++)
            {
                offsets[index] = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(index * 16));
                times[index] = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan((index * 16) + 8));
            }

            return new MatteFile(path, width, height, offsets, times, file.Position, file.Length);
        }

        /// <summary>The picture shown at a source time: the last that starts at or before it.</summary>
        public MatteFrame? At(Flicks sourceTime)
        {
            if (_offsets.Length == 0)
            {
                return null;
            }

            int index = Array.BinarySearch(_times, sourceTime.Value);
            index = index >= 0 ? index : Math.Max(0, ~index - 1);
            lock (_gate)
            {
                for (LinkedListNode<(int Index, MatteFrame Frame)>? node = _recent.First; node is not null; node = node.Next)
                {
                    if (node.Value.Index == index)
                    {
                        _recent.Remove(node);
                        _recent.AddFirst(node);
                        return node.Value.Frame;
                    }
                }
            }

            long start = _bodyStart + _offsets[index];
            long end = index + 1 < _offsets.Length ? _bodyStart + _offsets[index + 1] : _length;
            byte[] packed = new byte[end - start];
            using (var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                file.Position = start;
                file.ReadExactly(packed);
            }

            byte[] alpha = new byte[Width * Height];
            using (var brotli = new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                brotli.ReadExactly(alpha);
            }

            var frame = new MatteFrame(Width, Height, alpha, $"{_path}#{index}");
            lock (_gate)
            {
                _recent.AddFirst((index, frame));
                while (_recent.Count > 8)
                {
                    _recent.RemoveLast();
                }
            }

            return frame;
        }
    }
}

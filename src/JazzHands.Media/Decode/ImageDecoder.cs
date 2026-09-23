using FFmpeg.AutoGen;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using JazzHands.Media.Probe;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>The layout an <see cref="ImageDecoder"/> hands its frames over in.</summary>
/// <remarks>
/// Three, chosen so each maps onto exactly one thing the compositor can sample, and so nothing
/// the file said is thrown away on the way.
/// </remarks>
public enum ImagePrecision
{
    /// <summary>Eight bits a channel, interleaved RGBA. Ordinary PNG, JPEG, BMP.</summary>
    Byte,

    /// <summary>Sixteen bits a channel, interleaved RGBA. 16-bit PNG, TIFF, DPX.</summary>
    Short,

    /// <summary>Thirty two bit float, planar GBRA. OpenEXR and anything else with float samples.</summary>
    Float,
}

/// <summary>
/// Decodes stills and numbered image sequences.
/// </summary>
/// <remarks>
/// FFmpeg's image2 demuxer already reads a <c>%04d</c> pattern as one stream of frames, so a
/// sequence is decoded exactly like a movie and gets seeking, timestamps and the frame pool for
/// free. What this adds is the frame rate a run of files cannot state, and the conversion to a
/// layout the compositor can upload.
///
/// Stills and sequences are the one place decoded pixels are converted on the CPU. The image
/// codecs produce planar float, big endian 16-bit and other layouts no GPU samples, and unlike
/// video there is no hardware decoder whose output format could be steered instead. The
/// conversion preserves precision: a float file stays float, a 16-bit file stays 16-bit.
/// </remarks>
public sealed class ImageDecoder : IVideoSource
{
    /// <summary>What a still is worth on the timeline when nobody says otherwise.</summary>
    public static readonly Rational StillRate = new(1, 1);

    private readonly ILogger _log = Log.ForContext<ImageDecoder>();
    private readonly Demuxer _demuxer;
    private readonly VideoDecoder _decoder;
    private readonly PixelConverter? _converter;
    private readonly Rational _rate;
    private bool _disposed;

    private ImageDecoder(Demuxer demuxer, VideoDecoder decoder, Rational rate, AVPixelFormat sourceFormat)
    {
        _demuxer = demuxer;
        _decoder = decoder;
        _rate = rate;

        SourceFormat = sourceFormat;
        Precision = PrecisionOf(sourceFormat);
        OutputFormat = FormatFor(Precision);

        if (sourceFormat != OutputFormat)
        {
            _converter = new PixelConverter(decoder.Width, decoder.Height, sourceFormat, OutputFormat);
        }

        _log.Debug(
            "Opened {File} as {Width}x{Height} {Source}, delivering {Output} at {Rate}",
            demuxer.Path,
            decoder.Width,
            decoder.Height,
            sourceFormat,
            OutputFormat,
            rate);
    }

    /// <summary>The pixel format the file itself is in.</summary>
    public AVPixelFormat SourceFormat { get; }

    /// <summary>The pixel format frames come out in.</summary>
    public AVPixelFormat OutputFormat { get; }

    /// <summary>How much precision the frames carry.</summary>
    public ImagePrecision Precision { get; }

    /// <summary>Image width in pixels.</summary>
    public int Width => _decoder.Width;

    /// <summary>Image height in pixels.</summary>
    public int Height => _decoder.Height;

    /// <summary>The rate frames are presented at. One for a still.</summary>
    public Rational FrameRate => _rate;

    /// <summary>Opens a single image file.</summary>
    public static ImageDecoder OpenStill(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Open(path, StillRate, startNumber: null);
    }

    /// <summary>Opens a numbered run of image files.</summary>
    /// <param name="pattern">
    /// The full path with a printf style number in it, for example <c>render.%04d.exr</c>.
    /// </param>
    /// <param name="sequence">The run's first number, length and frame rate.</param>
    public static ImageDecoder OpenSequence(string pattern, ImageSequenceInfo sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(sequence);

        return Open(pattern, sequence.FrameRate, sequence.Start);
    }

    /// <summary>Opens whichever of the two a media item is.</summary>
    /// <param name="item">A media item whose kind is <see cref="MediaKind.Still"/> or <see cref="MediaKind.ImageSequence"/>.</param>
    /// <param name="path">The resolved absolute path or pattern.</param>
    public static ImageDecoder Open(MediaItem item, string path)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Kind switch
        {
            MediaKind.Still => OpenStill(path),
            MediaKind.ImageSequence when item.Sequence is not null => OpenSequence(path, item.Sequence),
            MediaKind.ImageSequence => throw new ArgumentException(
                $"Media item '{item.Name}' is a sequence but carries no sequence information.",
                nameof(item)),
            _ => throw new ArgumentException(
                $"Media item '{item.Name}' is a {item.Kind}, not an image.",
                nameof(item)),
        };
    }

    /// <inheritdoc />
    public VideoFrame? ReadFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        VideoFrame? frame = _decoder.ReadFrame();
        if (frame is null || _converter is null)
        {
            return frame;
        }

        using (frame)
        {
            return _converter.Convert(frame);
        }
    }

    /// <inheritdoc />
    public void Flush(Flicks resumeAt) => _decoder.Flush();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _converter?.Dispose();
        _decoder.Dispose();
        _demuxer.Dispose();
    }

    /// <summary>What the compositor gets for a given source format.</summary>
    internal static AVPixelFormat FormatFor(ImagePrecision precision) => precision switch
    {
        // Planar rather than packed because swscale has no packed float output. The compositor
        // binds the planes as three or four single channel textures.
        ImagePrecision.Float => AVPixelFormat.AV_PIX_FMT_GBRAPF32LE,
        ImagePrecision.Short => AVPixelFormat.AV_PIX_FMT_RGBA64LE,
        _ => AVPixelFormat.AV_PIX_FMT_RGBA,
    };

    /// <summary>How much precision a pixel format carries, from FFmpeg's own description of it.</summary>
    internal static unsafe ImagePrecision PrecisionOf(AVPixelFormat format)
    {
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        if (descriptor is null)
        {
            return ImagePrecision.Byte;
        }

        if ((descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_FLOAT) != 0)
        {
            return ImagePrecision.Float;
        }

        return descriptor->comp[0].depth > 8 ? ImagePrecision.Short : ImagePrecision.Byte;
    }

    private static unsafe ImageDecoder Open(string path, Rational rate, int? startNumber)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // image2 defaults to 25, which would make a sequence's timestamps disagree with the
            // duration the media item was imported with.
            ["framerate"] = $"{rate.Num}/{rate.Den}",
        };

        if (startNumber is { } start)
        {
            options["start_number"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // A pattern is not a file, so the existence check has to be skipped; avformat_open_input
        // reports a run that is not there well enough on its own.
        var demuxer = new Demuxer(path, options, fileMustExist: startNumber is null);
        try
        {
            int stream = demuxer.FindBestStream(StreamKind.Video);
            if (stream < 0)
            {
                throw new FfmpegException(Av.InvalidArgument, "av_find_best_stream", $"'{path}' has no image in it.");
            }

            var format = (AVPixelFormat)demuxer.GetStream(stream)->codecpar->format;
            var decoder = new VideoDecoder(demuxer, stream, hardware: null, poolDepth: 4);

            try
            {
                return new ImageDecoder(demuxer, decoder, rate, format);
            }
            catch
            {
                decoder.Dispose();
                throw;
            }
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }
}

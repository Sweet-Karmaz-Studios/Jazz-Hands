using FFmpeg.AutoGen;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Encode;

/// <summary>
/// Writes a picture held in memory as a PNG, with FFmpeg's own encoder.
/// </summary>
/// <remarks>
/// For the pictures the engine makes itself rather than renders through an export: contact sheets
/// and the like. The engine is headless and cannot reach WPF's encoders; FFmpeg is already loaded.
/// The picture is opaque, so it is written as RGB and tagged sRGB.
/// </remarks>
public static unsafe class PngWriter
{
    /// <summary>Encodes a BGRA picture, top row first, and returns the file's bytes.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException($"A {width}x{height} picture is {width * height * 4} bytes, not {bgra.Length}.", nameof(bgra));
        }

        FfmpegLoader.Initialize();
        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_PNG);
        if (codec == null)
        {
            throw new FfmpegException("This FFmpeg build has no PNG encoder.");
        }

        using var context = new AvCodecContext(codec);
        using var frame = new AvFrame();
        using var packet = new AvPacket();

        AVCodecContext* handle = context.Handle;
        handle->width = width;
        handle->height = height;
        handle->pix_fmt = AVPixelFormat.AV_PIX_FMT_RGB24;
        handle->time_base = new AVRational { num = 1, den = 25 };
        handle->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        handle->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1;
        handle->colorspace = AVColorSpace.AVCOL_SPC_RGB;
        handle->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        handle->thread_count = 1;
        Av.Check(ffmpeg.avcodec_open2(handle, codec, null), "avcodec_open2", "png");

        AVFrame* picture = frame.Handle;
        picture->width = width;
        picture->height = height;
        picture->format = (int)AVPixelFormat.AV_PIX_FMT_RGB24;
        picture->color_primaries = handle->color_primaries;
        picture->color_trc = handle->color_trc;
        picture->colorspace = handle->colorspace;
        picture->color_range = handle->color_range;
        Av.Check(ffmpeg.av_frame_get_buffer(picture, 0), "av_frame_get_buffer");

        byte* rows = picture->data[0];
        int stride = picture->linesize[0];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> source = bgra.Slice(y * width * 4, width * 4);
            byte* target = rows + ((long)y * stride);
            for (int x = 0; x < width; x++)
            {
                target[(x * 3) + 0] = source[(x * 4) + 2];
                target[(x * 3) + 1] = source[(x * 4) + 1];
                target[(x * 3) + 2] = source[(x * 4) + 0];
            }
        }

        Av.Check(ffmpeg.avcodec_send_frame(handle, picture), "avcodec_send_frame", "png");
        Av.Check(ffmpeg.avcodec_receive_packet(handle, packet.Handle), "avcodec_receive_packet", "png");
        return new ReadOnlySpan<byte>(packet.Handle->data, packet.Handle->size).ToArray();
    }

    /// <summary>Writes a BGRA picture to a PNG file.</summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, Encode(width, height, bgra));
    }
}

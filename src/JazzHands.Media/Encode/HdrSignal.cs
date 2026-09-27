using FFmpeg.AutoGen;

namespace JazzHands.Media.Encode;

/// <summary>
/// HDR10 for an encode: BT.2020 primaries, the SMPTE ST 2084 (PQ) curve and the BT.2020
/// non-constant luminance matrix, with the mastering display (SMPTE ST 2086) and the content light
/// levels (CTA-861.3) written into the stream and the file.
/// </summary>
/// <remarks>
/// The picture itself must already be PQ coded BT.2020 Y'CbCr at ten bits: this only says so.
/// Chromaticities are CIE 1931 xy.
/// </remarks>
/// <param name="MaxLuminance">The mastering display's peak, in candelas per square metre.</param>
/// <param name="MinLuminance">Its black, in candelas per square metre.</param>
/// <param name="RedX">Its red primary, x.</param>
/// <param name="RedY">Its red primary, y.</param>
/// <param name="GreenX">Its green primary, x.</param>
/// <param name="GreenY">Its green primary, y.</param>
/// <param name="BlueX">Its blue primary, x.</param>
/// <param name="BlueY">Its blue primary, y.</param>
/// <param name="WhiteX">Its white point, x.</param>
/// <param name="WhiteY">Its white point, y.</param>
/// <param name="MaxCll">The brightest pixel of the content, in candelas per square metre; 0 for unknown.</param>
/// <param name="MaxFall">The brightest frame average of the content; 0 for unknown.</param>
public sealed record HdrSignal(
    double MaxLuminance,
    double MinLuminance,
    double RedX,
    double RedY,
    double GreenX,
    double GreenY,
    double BlueX,
    double BlueY,
    double WhiteX,
    double WhiteY,
    int MaxCll,
    int MaxFall)
{
    /// <summary>
    /// A P3-D65 mastering display of the given peak and a black of 0.0001, which is what the ACES
    /// 2.0 HDR10 output transform renders for; the brightest pixel is at most that peak, and the
    /// frame average is not measured (0).
    /// </summary>
    public static HdrSignal P3D65(double peak) =>
        new(peak, 0.0001, 0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.3127, 0.3290, (int)Math.Round(peak), 0);

    /// <summary>Tags a frame as BT.2020 PQ, limited range.</summary>
    internal static unsafe void Tag(AVFrame* frame)
    {
        frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
        frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
        frame->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
        frame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
    }

    /// <summary>Tags an encoder as BT.2020 PQ, limited range.</summary>
    internal static unsafe void Tag(AVCodecContext* context)
    {
        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
        context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
        context->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
        context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
    }

    /// <summary>
    /// Gives an encoder the mastering display and light levels before it opens, where libx265,
    /// SVT-AV1 and NVENC read them for their headers and SEI.
    /// </summary>
    internal unsafe void Attach(AVCodecContext* context)
    {
        AVFrameSideData* mastering = ffmpeg.av_frame_side_data_new(
            &context->decoded_side_data, &context->nb_decoded_side_data,
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA, (ulong)sizeof(AVMasteringDisplayMetadata), 0);
        Write((AVMasteringDisplayMetadata*)Interop.Av.CheckAlloc(mastering, "av_frame_side_data_new")->data);

        AVFrameSideData* light = ffmpeg.av_frame_side_data_new(
            &context->decoded_side_data, &context->nb_decoded_side_data,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL, (ulong)sizeof(AVContentLightMetadata), 0);
        Write((AVContentLightMetadata*)Interop.Av.CheckAlloc(light, "av_frame_side_data_new")->data);
    }

    /// <summary>Puts the metadata on a frame too, once, for an encoder that reads it per frame.</summary>
    internal unsafe void Attach(AVFrame* frame)
    {
        if (ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA) is null)
        {
            AVFrameSideData* mastering = ffmpeg.av_frame_new_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA, (ulong)sizeof(AVMasteringDisplayMetadata));
            Write((AVMasteringDisplayMetadata*)Interop.Av.CheckAlloc(mastering, "av_frame_new_side_data")->data);
        }

        if (ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL) is null)
        {
            AVFrameSideData* light = ffmpeg.av_frame_new_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL, (ulong)sizeof(AVContentLightMetadata));
            Write((AVContentLightMetadata*)Interop.Av.CheckAlloc(light, "av_frame_new_side_data")->data);
        }
    }

    /// <summary>Writes the metadata onto a stream for the container (MP4's mdcv and clli boxes, Matroska's colour elements).</summary>
    internal unsafe void Attach(AVCodecParameters* parameters)
    {
        // What the encoder put there itself is kept.
        if (ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA) is null)
        {
            AVPacketSideData* mastering = ffmpeg.av_packet_side_data_new(
                &parameters->coded_side_data, &parameters->nb_coded_side_data,
                AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA, (ulong)sizeof(AVMasteringDisplayMetadata), 0);
            Write((AVMasteringDisplayMetadata*)Interop.Av.CheckAlloc(mastering, "av_packet_side_data_new")->data);
        }

        if (ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL) is null)
        {
            AVPacketSideData* light = ffmpeg.av_packet_side_data_new(
                &parameters->coded_side_data, &parameters->nb_coded_side_data,
                AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL, (ulong)sizeof(AVContentLightMetadata), 0);
            Write((AVContentLightMetadata*)Interop.Av.CheckAlloc(light, "av_packet_side_data_new")->data);
        }
    }

    // ST 2086 as HEVC and AV1 carry it: chromaticity in steps of 0.00002, luminance in 0.0001.
    private static AVRational Chroma(double value) => new() { num = (int)Math.Round(value * 50000), den = 50000 };

    private static AVRational Nits(double value) => new() { num = (int)Math.Round(value * 10000), den = 10000 };

    private unsafe void Write(AVMasteringDisplayMetadata* metadata)
    {
        // display_primaries is AVRational[3][2] and white_point AVRational[2], laid out in order.
        var primaries = (AVRational*)&metadata->display_primaries;
        primaries[0] = Chroma(RedX);
        primaries[1] = Chroma(RedY);
        primaries[2] = Chroma(GreenX);
        primaries[3] = Chroma(GreenY);
        primaries[4] = Chroma(BlueX);
        primaries[5] = Chroma(BlueY);
        var white = (AVRational*)&metadata->white_point;
        white[0] = Chroma(WhiteX);
        white[1] = Chroma(WhiteY);
        metadata->min_luminance = Nits(MinLuminance);
        metadata->max_luminance = Nits(MaxLuminance);
        metadata->has_primaries = 1;
        metadata->has_luminance = 1;
    }

    private unsafe void Write(AVContentLightMetadata* light)
    {
        light->MaxCLL = (uint)Math.Max(0, MaxCll);
        light->MaxFALL = (uint)Math.Max(0, MaxFall);
    }
}

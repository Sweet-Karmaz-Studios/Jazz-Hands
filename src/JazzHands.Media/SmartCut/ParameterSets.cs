namespace JazzHands.Media.SmartCut;

/// <summary>
/// What a decoder has to agree on to play two bitstreams one after the other: the codec, its
/// profile, the chroma format, the bit depth and the picture size.
/// </summary>
/// <remarks>
/// Smart cut joins frames a matched encoder made to packets copied from the source. The two sets
/// of parameter sets need not be the same bytes (each part carries its own, switched at a
/// keyframe), but they must describe the same kind of picture, or a player that sized its decoder
/// for one gets the other. The level is left out: it only bounds what the decoder must be able to
/// do, and an encoder rounds it its own way.
/// </remarks>
/// <param name="Codec">h264, hevc or av1.</param>
/// <param name="Profile">profile_idc for H.264 and HEVC, seq_profile for AV1.</param>
/// <param name="ChromaFormat">0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4.</param>
/// <param name="BitDepth">Bits a luma sample.</param>
/// <param name="Width">The picture's width after cropping (AV1: the largest frame's).</param>
/// <param name="Height">The picture's height after cropping.</param>
/// <param name="Progressive">False for an H.264 stream coded as fields.</param>
public sealed record StreamSignature(string Codec, int Profile, int ChromaFormat, int BitDepth, int Width, int Height, bool Progressive = true)
{
    /// <summary>What differs from another signature, in words, or null when nothing does.</summary>
    public string? Differences(StreamSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var differences = new List<string>();
        if (Codec != other.Codec)
        {
            differences.Add($"codec {Codec} against {other.Codec}");
        }

        if (Profile != other.Profile)
        {
            differences.Add($"profile {Profile} against {other.Profile}");
        }

        if (ChromaFormat != other.ChromaFormat)
        {
            differences.Add($"chroma format {ChromaFormat} against {other.ChromaFormat}");
        }

        if (BitDepth != other.BitDepth)
        {
            differences.Add($"{BitDepth} bit against {other.BitDepth} bit");
        }

        if (Width != other.Width || Height != other.Height)
        {
            differences.Add($"{Width}x{Height} against {other.Width}x{other.Height}");
        }

        if (Progressive != other.Progressive)
        {
            differences.Add(Progressive ? "frames against fields" : "fields against frames");
        }

        return differences.Count == 0 ? null : string.Join(", ", differences);
    }
}

/// <summary>The parameter sets a stream's decoder configuration holds, ready to put back in band.</summary>
/// <param name="Units">The SPS, PPS and VPS NAL units (without start codes or lengths), or AV1's sequence header OBU.</param>
/// <param name="LengthSize">For H.264 and HEVC, how many bytes each NAL unit's length takes in a packet: 4, 2 or 1.</param>
/// <param name="Signature">What they describe, or null when they could not be read.</param>
public sealed record DecoderConfiguration(IReadOnlyList<byte[]> Units, int LengthSize, StreamSignature? Signature);

/// <summary>
/// Reads H.264 and HEVC parameter sets and AV1 sequence headers: out of a container's decoder
/// configuration (avcC, hvcC, av1C), out of an encoder's Annex B packets, and down to a
/// <see cref="StreamSignature"/>.
/// </summary>
public static class ParameterSets
{
    /// <summary>The HEVC end of sequence NAL unit, which lets the CRA after it start a new sequence.</summary>
    public static ReadOnlySpan<byte> HevcEndOfSequence => [0x48, 0x01];

    /// <summary>Reads a container's decoder configuration for a codec.</summary>
    /// <param name="codec">h264, hevc or av1.</param>
    /// <param name="extradata">The stream's extradata: avcC, hvcC, av1C, or Annex B.</param>
    public static DecoderConfiguration Read(string codec, ReadOnlySpan<byte> extradata)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return codec switch
        {
            "h264" => ReadNal(extradata, isHevc: false),
            "hevc" => ReadNal(extradata, isHevc: true),
            "av1" => ReadAv1(extradata),
            _ => new DecoderConfiguration([], 4, null),
        };
    }

    /// <summary>The signature the parameter sets in an Annex B bitstream (an encoder's packet or extradata) describe, or null.</summary>
    public static StreamSignature? FromAnnexB(string codec, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (codec == "av1")
        {
            return FromObus(data);
        }

        foreach (Range unit in SplitAnnexB(data))
        {
            ReadOnlySpan<byte> nal = data[unit];
            if (Signature(codec, nal) is { } signature)
            {
                return signature;
            }
        }

        return null;
    }

    /// <summary>The signature one NAL unit describes, when it is an SPS; null otherwise.</summary>
    public static StreamSignature? Signature(string codec, ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty)
        {
            return null;
        }

        return codec switch
        {
            "h264" when (nal[0] & 0x1F) == 7 => H264Sps(nal),
            "hevc" when nal.Length > 2 && ((nal[0] >> 1) & 0x3F) == 33 => HevcSps(nal),
            _ => null,
        };
    }

    /// <summary>The NAL units of an Annex B bitstream, as ranges of it, without their start codes.</summary>
    public static List<Range> SplitAnnexB(ReadOnlySpan<byte> data)
    {
        var units = new List<Range>();
        int start = -1;
        int index = 0;
        while (index + 2 < data.Length)
        {
            if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
            {
                if (start >= 0)
                {
                    int end = index;

                    // A four byte start code's leading zero belongs to it, not to the unit before.
                    if (end > start && data[end - 1] == 0)
                    {
                        end--;
                    }

                    units.Add(start..end);
                }

                index += 3;
                start = index;
                continue;
            }

            index++;
        }

        if (start >= 0 && start < data.Length)
        {
            units.Add(start..data.Length);
        }

        return units;
    }

    /// <summary>True when a packet starts with an Annex B start code rather than a length.</summary>
    public static bool IsAnnexB(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));

    /// <summary>Writes NAL units one after another, each after its length in <paramref name="lengthSize"/> bytes, big endian.</summary>
    public static byte[] LengthPrefixed(IEnumerable<byte[]> units, int lengthSize)
    {
        ArgumentNullException.ThrowIfNull(units);
        using var stream = new MemoryStream();
        foreach (byte[] unit in units)
        {
            for (int shift = (lengthSize - 1) * 8; shift >= 0; shift -= 8)
            {
                stream.WriteByte((byte)(unit.Length >> shift));
            }

            stream.Write(unit);
        }

        return stream.ToArray();
    }

    /// <summary>Turns an Annex B packet into length prefixed NAL units.</summary>
    public static byte[] AnnexBToLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize)
    {
        var units = new List<byte[]>();
        foreach (Range unit in SplitAnnexB(data))
        {
            units.Add(data[unit].ToArray());
        }

        return LengthPrefixed(units, lengthSize);
    }

    private static DecoderConfiguration ReadNal(ReadOnlySpan<byte> extradata, bool isHevc)
    {
        string codec = isHevc ? "hevc" : "h264";
        var units = new List<byte[]>();
        int lengthSize = 4;

        if (IsAnnexB(extradata))
        {
            foreach (Range unit in SplitAnnexB(extradata))
            {
                units.Add(extradata[unit].ToArray());
            }
        }
        else if (!isHevc && extradata.Length >= 7 && extradata[0] == 1)
        {
            // avcC: version, profile, compatibility, level, length size, then counted SPS and PPS.
            lengthSize = (extradata[4] & 0x3) + 1;
            int at = 5;
            int sequences = extradata[at++] & 0x1F;
            at = ReadCounted(extradata, at, sequences, units);
            if (at < extradata.Length)
            {
                int pictures = extradata[at++];
                ReadCounted(extradata, at, pictures, units);
            }
        }
        else if (isHevc && extradata.Length >= 23)
        {
            // hvcC: 22 bytes of header, the length size in the last one's low bits, then arrays of
            // NAL units by type (VPS, SPS, PPS, SEI).
            lengthSize = (extradata[21] & 0x3) + 1;
            int arrays = extradata[22];
            int at = 23;
            for (int array = 0; array < arrays && at + 3 <= extradata.Length; array++)
            {
                at++;
                int count = (extradata[at] << 8) | extradata[at + 1];
                at += 2;
                at = ReadCounted(extradata, at, count, units);
            }
        }

        StreamSignature? signature = null;
        foreach (byte[] unit in units)
        {
            signature ??= Signature(codec, unit);
        }

        // SEI in a configuration is for the first picture only; putting it back later would lie.
        byte[][] sets = [.. units.Where(unit => IsParameterSet(codec, unit))];
        return new DecoderConfiguration(sets, lengthSize, signature);
    }

    private static bool IsParameterSet(string codec, byte[] unit) =>
        unit.Length > 0 && (codec == "h264"
            ? (unit[0] & 0x1F) is 7 or 8
            : ((unit[0] >> 1) & 0x3F) is 32 or 33 or 34);

    private static int ReadCounted(ReadOnlySpan<byte> data, int at, int count, List<byte[]> into)
    {
        for (int index = 0; index < count && at + 2 <= data.Length; index++)
        {
            int length = (data[at] << 8) | data[at + 1];
            at += 2;
            if (at + length > data.Length)
            {
                break;
            }

            into.Add(data.Slice(at, length).ToArray());
            at += length;
        }

        return at;
    }

    private static DecoderConfiguration ReadAv1(ReadOnlySpan<byte> extradata)
    {
        // av1C: four bytes of header, then the configuration OBUs (the sequence header). An
        // extradata of bare OBUs (what an encoder's global header gives) has no marker bit.
        ReadOnlySpan<byte> obus = extradata.Length >= 4 && (extradata[0] & 0x80) != 0 ? extradata[4..] : extradata;
        var units = new List<byte[]>();
        StreamSignature? signature = null;
        foreach ((int Type, Range Whole, Range Payload) obu in Obus(obus))
        {
            if (obu.Type == 1)
            {
                units.Add(obus[obu.Whole].ToArray());
                signature ??= Av1SequenceHeader(obus[obu.Payload]);
            }
        }

        return new DecoderConfiguration(units, 0, signature);
    }

    private static StreamSignature? FromObus(ReadOnlySpan<byte> data)
    {
        foreach ((int Type, Range Whole, Range Payload) obu in Obus(data))
        {
            if (obu.Type == 1)
            {
                return Av1SequenceHeader(data[obu.Payload]);
            }
        }

        return null;
    }

    /// <summary>The OBUs of a low overhead AV1 bitstream: type, the whole OBU, and its payload.</summary>
    private static List<(int Type, Range Whole, Range Payload)> Obus(ReadOnlySpan<byte> data)
    {
        var obus = new List<(int, Range, Range)>();
        int at = 0;
        while (at < data.Length)
        {
            int start = at;
            byte header = data[at++];
            int type = (header >> 3) & 0xF;
            bool extension = (header & 0x4) != 0;
            bool sized = (header & 0x2) != 0;
            if (extension)
            {
                at++;
            }

            long size;
            if (sized)
            {
                size = 0;
                for (int index = 0; index < 8 && at < data.Length; index++)
                {
                    byte next = data[at++];
                    size |= (long)(next & 0x7F) << (index * 7);
                    if ((next & 0x80) == 0)
                    {
                        break;
                    }
                }
            }
            else
            {
                size = data.Length - at;
            }

            if (size < 0 || at + size > data.Length)
            {
                break;
            }

            obus.Add((type, start..(at + (int)size), at..(at + (int)size)));
            at += (int)size;
        }

        return obus;
    }

    private static StreamSignature? H264Sps(ReadOnlySpan<byte> nal)
    {
        try
        {
            var bits = new BitReader(Unescape(nal[1..]));
            int profile = bits.Read(8);
            bits.Skip(8);
            bits.Skip(8);
            bits.Golomb();

            int chroma = 1;
            int depth = 8;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chroma = bits.Golomb();
                if (chroma == 3)
                {
                    bits.Skip(1);
                }

                depth = bits.Golomb() + 8;
                bits.Golomb();
                bits.Skip(1);
                if (bits.Flag())
                {
                    for (int list = 0; list < (chroma != 3 ? 8 : 12); list++)
                    {
                        if (bits.Flag())
                        {
                            SkipScalingList(bits, list < 6 ? 16 : 64);
                        }
                    }
                }
            }

            bits.Golomb();
            int order = bits.Golomb();
            if (order == 0)
            {
                bits.Golomb();
            }
            else if (order == 1)
            {
                bits.Skip(1);
                bits.SignedGolomb();
                bits.SignedGolomb();
                int cycle = bits.Golomb();
                for (int index = 0; index < cycle; index++)
                {
                    bits.SignedGolomb();
                }
            }

            bits.Golomb();
            bits.Skip(1);
            int widthInMbs = bits.Golomb() + 1;
            int heightInUnits = bits.Golomb() + 1;
            bool frames = bits.Flag();
            if (!frames)
            {
                bits.Skip(1);
            }

            bits.Skip(1);
            int left = 0, right = 0, top = 0, bottom = 0;
            if (bits.Flag())
            {
                left = bits.Golomb();
                right = bits.Golomb();
                top = bits.Golomb();
                bottom = bits.Golomb();
            }

            int cropX = chroma is 1 or 2 ? 2 : 1;
            int cropY = (chroma == 1 ? 2 : 1) * (frames ? 1 : 2);
            int width = (widthInMbs * 16) - (cropX * (left + right));
            int height = ((frames ? 1 : 2) * heightInUnits * 16) - (cropY * (top + bottom));
            return new StreamSignature("h264", profile, chroma, depth, width, height, frames);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static void SkipScalingList(BitReader bits, int size)
    {
        int last = 8;
        int next = 8;
        for (int index = 0; index < size; index++)
        {
            if (next != 0)
            {
                next = (last + bits.SignedGolomb() + 256) % 256;
            }

            last = next == 0 ? last : next;
        }
    }

    private static StreamSignature? HevcSps(ReadOnlySpan<byte> nal)
    {
        try
        {
            var bits = new BitReader(Unescape(nal[2..]));
            bits.Skip(4);
            int subLayers = bits.Read(3);
            bits.Skip(1);

            // profile_tier_level: space, tier and profile, 32 compatibility flags, 48 bits of
            // constraint flags, the level; then which sub layers carry their own.
            bits.Skip(3);
            int profile = bits.Read(5);
            bits.Skip(32);
            bits.Skip(48);
            bits.Skip(8);
            var profilePresent = new bool[subLayers];
            var levelPresent = new bool[subLayers];
            for (int layer = 0; layer < subLayers; layer++)
            {
                profilePresent[layer] = bits.Flag();
                levelPresent[layer] = bits.Flag();
            }

            if (subLayers > 0)
            {
                for (int layer = subLayers; layer < 8; layer++)
                {
                    bits.Skip(2);
                }
            }

            for (int layer = 0; layer < subLayers; layer++)
            {
                if (profilePresent[layer])
                {
                    bits.Skip(88);
                }

                if (levelPresent[layer])
                {
                    bits.Skip(8);
                }
            }

            bits.Golomb();
            int chroma = bits.Golomb();
            if (chroma == 3)
            {
                bits.Skip(1);
            }

            int width = bits.Golomb();
            int height = bits.Golomb();
            if (bits.Flag())
            {
                int subWidth = chroma is 1 or 2 ? 2 : 1;
                int subHeight = chroma == 1 ? 2 : 1;
                int left = bits.Golomb();
                int right = bits.Golomb();
                int top = bits.Golomb();
                int bottom = bits.Golomb();
                width -= subWidth * (left + right);
                height -= subHeight * (top + bottom);
            }

            int depth = bits.Golomb() + 8;
            return new StreamSignature("hevc", profile, chroma, depth, width, height);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static StreamSignature? Av1SequenceHeader(ReadOnlySpan<byte> payload)
    {
        try
        {
            var bits = new BitReader(payload.ToArray());
            int profile = bits.Read(3);
            bits.Skip(1);
            bool reduced = bits.Flag();
            bool decoderModel = false;
            int bufferDelayLength = 0;
            if (reduced)
            {
                bits.Skip(5);
            }
            else
            {
                if (bits.Flag())
                {
                    bits.Skip(32);
                    bits.Skip(32);
                    if (bits.Flag())
                    {
                        bits.UnsignedVariableLength();
                    }

                    decoderModel = bits.Flag();
                    if (decoderModel)
                    {
                        bufferDelayLength = bits.Read(5) + 1;
                        bits.Skip(32);
                        bits.Skip(5);
                        bits.Skip(5);
                    }
                }

                bool displayDelay = bits.Flag();
                int points = bits.Read(5) + 1;
                for (int point = 0; point < points; point++)
                {
                    bits.Skip(12);
                    int level = bits.Read(5);
                    if (level > 7)
                    {
                        bits.Skip(1);
                    }

                    if (decoderModel && bits.Flag())
                    {
                        bits.Skip(bufferDelayLength);
                        bits.Skip(bufferDelayLength);
                        bits.Skip(1);
                    }

                    if (displayDelay && bits.Flag())
                    {
                        bits.Skip(4);
                    }
                }
            }

            int widthBits = bits.Read(4) + 1;
            int heightBits = bits.Read(4) + 1;
            int width = bits.Read(widthBits) + 1;
            int height = bits.Read(heightBits) + 1;
            if (!reduced && bits.Flag())
            {
                bits.Skip(4);
                bits.Skip(3);
            }

            bits.Skip(3);
            if (!reduced)
            {
                bits.Skip(4);
                bool orderHint = bits.Flag();
                if (orderHint)
                {
                    bits.Skip(2);
                }

                int screenContent = bits.Flag() ? 2 : bits.Read(1);
                if (screenContent > 0 && !bits.Flag())
                {
                    bits.Skip(1);
                }

                if (orderHint)
                {
                    bits.Skip(3);
                }
            }

            bits.Skip(3);

            // color_config
            bool high = bits.Flag();
            int depth = profile == 2 && high ? (bits.Flag() ? 12 : 10) : high ? 10 : 8;
            bool mono = profile != 1 && bits.Flag();
            int primaries = 2, transfer = 2, matrix = 2;
            if (bits.Flag())
            {
                primaries = bits.Read(8);
                transfer = bits.Read(8);
                matrix = bits.Read(8);
            }

            int chroma;
            if (mono)
            {
                chroma = 0;
            }
            else if (primaries == 1 && transfer == 13 && matrix == 0)
            {
                chroma = 3;
            }
            else
            {
                bits.Skip(1);
                bool subX = true;
                bool subY = true;
                if (profile == 1)
                {
                    subX = false;
                    subY = false;
                }
                else if (profile == 2)
                {
                    if (depth == 12)
                    {
                        subX = bits.Flag();
                        subY = subX && bits.Flag();
                    }
                    else
                    {
                        subY = false;
                    }
                }

                chroma = subX && subY ? 1 : subX ? 2 : 3;
            }

            return new StreamSignature("av1", profile, chroma, depth, width, height);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Removes the emulation prevention bytes (00 00 03 becomes 00 00) that keep a start code out of a NAL unit.</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var raw = new List<byte>(data.Length);
        int zeros = 0;
        foreach (byte value in data)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = value == 0 ? zeros + 1 : 0;
            raw.Add(value);
        }

        return [.. raw];
    }

    /// <summary>Reads bits most significant first, with the Exp-Golomb codes H.264 and HEVC use.</summary>
    private sealed class BitReader(byte[] data)
    {
        private long _position;

        public int Read(int count)
        {
            int value = 0;
            for (int index = 0; index < count; index++)
            {
                value = (value << 1) | Bit();
            }

            return value;
        }

        public bool Flag() => Bit() == 1;

        public void Skip(int count)
        {
            if (_position + count > (long)data.Length * 8)
            {
                throw new EndOfStreamException();
            }

            _position += count;
        }

        public int Golomb()
        {
            int zeros = 0;
            while (Bit() == 0)
            {
                zeros++;
                if (zeros > 31)
                {
                    throw new EndOfStreamException();
                }
            }

            return zeros == 0 ? 0 : (int)((1L << zeros) - 1 + Read(zeros));
        }

        public int SignedGolomb()
        {
            int code = Golomb();
            return (code & 1) == 1 ? (code + 1) / 2 : -(code / 2);
        }

        /// <summary>AV1's uvlc(): leading zeros, then that many bits.</summary>
        public long UnsignedVariableLength()
        {
            int zeros = 0;
            while (Bit() == 0)
            {
                zeros++;
                if (zeros >= 32)
                {
                    return uint.MaxValue;
                }
            }

            return (1L << zeros) - 1 + Read(zeros);
        }

        private int Bit()
        {
            if (_position >= (long)data.Length * 8)
            {
                throw new EndOfStreamException();
            }

            int value = (data[_position >> 3] >> (7 - (int)(_position & 7))) & 1;
            _position++;
            return value;
        }
    }
}

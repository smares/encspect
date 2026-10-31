// Derived from libwavpack 5.6.0 (open_utils.c, decorr_utils.c, entropy_utils.c and unpack_dsd.c), Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using Encspect.Core;

namespace Encspect.Formats.WavPack;

// A sub-block as libwavpack's read_metadata_buff finds it (open_utils.c): where its data lies in the block, without
// the byte that pads an odd size.
internal readonly record struct SubBlockSpan(int Id, int Function, int HeaderLength, int DataStart, int DataLength, int Next);

// The syntax of a block's sub-blocks that a decoder needs, read as libwavpack's unpack_init and process_metadata read
// them (open_utils.c, decorr_utils.c, entropy_utils.c, unpack_dsd.c): the decorrelation passes with their weights and
// samples, the entropy variables, the hybrid profile, the float and integer details, where the bitstreams lie, and what
// the block says about the stream (channels, new config, sample rate, MD5). Anything libwavpack refuses is an
// InvalidDataException. One instance holds the contents of one block at a time and is reused for the next, so reading
// allocates nothing per block; the spans of a block it points to are valid only while the block's bytes are.
internal sealed class BlockContents
{
    public const int MaxPasses = Decode.MaxTerms;
    public const int MaxTerm = Decode.MaxTerm;

    // The decorrelation passes in the order the decoder runs them: libwavpack stores the terms the encoder sent last
    // first (decorr_utils.c read_decorr_terms).
    public int Passes { get; private set; }

    public int[] Terms { get; } = new int[MaxPasses];

    public int[] Deltas { get; } = new int[MaxPasses];

    public int[] WeightsA { get; } = new int[MaxPasses];

    public int[] WeightsB { get; } = new int[MaxPasses];

    public int[] SamplesA { get; } = new int[MaxPasses * MaxTerm];

    public int[] SamplesB { get; } = new int[MaxPasses * MaxTerm];

    // The medians of the entropy coder of each channel (read_entropy_vars) and the hybrid profile (read_hybrid_profile).
    public uint[] Medians { get; } = new uint[6];

    public uint[] SlowLevels { get; } = new uint[2];

    public uint[] BitrateAccumulators { get; } = new uint[2];

    public uint[] BitrateDeltas { get; } = new uint[2];

    public int[] ShapingAccumulators { get; } = new int[2];

    public int[] ShapingDeltas { get; } = new int[2];

    public int[] Errors { get; } = new int[2];

    public bool HasFloatInfo { get; private set; }

    public byte FloatFlags { get; private set; }

    public byte FloatShift { get; private set; }

    public byte FloatMaxExponent { get; private set; }

    public byte FloatNormalizeExponent { get; private set; }

    public byte Int32SentBits { get; private set; }

    public byte Int32Zeros { get; private set; }

    public byte Int32Ones { get; private set; }

    public byte Int32Dups { get; private set; }

    // The main bitstream, the extra bitstream (after its CRC) and the DSD block, as ranges of the block; a Length of 0
    // means the block has none. The correction bitstream is checked and not used: a main file decodes without it.
    public (int Start, int Length) Bitstream { get; private set; }

    public (int Start, int Length) ExtraBitstream { get; private set; }

    public uint ExtraCrc { get; private set; }

    public (int Start, int Length) Dsd { get; private set; }

    public bool HasCorrection { get; private set; }

    // What the block says about the stream, for the decoder's stream-level state.
    public ChannelInfoValue? ChannelInfo { get; private set; }

    public int? SampleRate { get; private set; }

    public bool HasNewConfig { get; private set; }

    public int QualifyMode { get; private set; }

    public int LayoutChannels { get; private set; }

    public byte[] Reordering { get; } = new byte[256];

    public int ReorderingLength { get; private set; }

    // The last MD5 sub-block of the block, 16 bytes; Alternate says which kind it was.
    public (int Start, bool IsAlternate)? Md5 { get; private set; }

    // The sub-block that holds the MD5 and the CRC of the audio are located by the decoder's findings with these.
    public int Md5Position { get; private set; }

    public int Md5HeaderLength { get; private set; }

    public int Md5Size { get; private set; }

    // The same for the extra bitstream sub-block, whose CRC is the first 4 bytes of its data.
    public int ExtraPosition { get; private set; }

    public int ExtraHeaderLength { get; private set; }

    public int ExtraSize { get; private set; }

    // libwavpack's read_metadata_buff: the sub-block at position of block, or false where the block holds no further one
    // (fewer than two bytes left, a size that runs past the block, or an odd size of 0). A sub-block that does not fit
    // ends the reading without a refusal.
    public static bool TryRead(ReadOnlySpan<byte> block, int position, out SubBlockSpan sub)
    {
        sub = default;
        if (block.Length - position < 2)
        {
            return false;
        }

        int id = block[position];
        int length = block[position + 1] << 1;
        int dataAt = position + 2;
        if ((id & SubBlockIds.LargeBit) != 0)
        {
            if (block.Length - dataAt < 2)
            {
                return false;
            }

            length += (block[dataAt] << 9) + (block[dataAt + 1] << 17);
            dataAt += 2;
        }

        int function = id & 0x3F;
        if ((id & SubBlockIds.OddSizeBit) != 0)
        {
            if (length == 0)
            {
                return false;
            }

            length--;
        }

        int padded = length + (length & 1);
        if (length != 0 && block.Length - dataAt < padded)
        {
            return false;
        }

        sub = new SubBlockSpan(id, function, dataAt - position, dataAt, length, length == 0 ? dataAt : dataAt + padded);
        return true;
    }

    // Reads the block. channelsAreKnown says that an earlier block settled the channels, so a channel info is only
    // checked for its length (read_channel_info).
    public void Read(ReadOnlySpan<byte> block, in BlockHeader header, bool channelsAreKnown)
    {
        Reset();
        uint flags = header.Flags;
        bool isMono = (flags & BlockHeader.MonoFlag) != 0 || (flags & BlockHeader.FalseStereoFlag) != 0;
        if ((flags & (BlockHeader.MonoFlag | BlockHeader.FalseStereoFlag)) == (BlockHeader.MonoFlag | BlockHeader.FalseStereoFlag))
        {
            throw new InvalidDataException("The mono flag and the false stereo flag are both set");
        }

        int position = BlockHeader.Length;
        while (TryRead(block, position, out SubBlockSpan sub))
        {
            ReadOnlySpan<byte> data = block.Slice(sub.DataStart, sub.DataLength);
            Process(block, header, sub, data, isMono, channelsAreKnown);
            position = sub.Next;
        }

        if (header.HasAudio)
        {
            if (header.IsDsd ? Dsd.Length == 0 : Bitstream.Length == 0)
            {
                throw new InvalidDataException(HasCorrection ? "The block holds only correction data, which cannot be decoded alone" : "The block counts samples but holds no bitstream");
            }
        }
    }

    private void Reset()
    {
        Passes = 0;
        Array.Clear(Terms);
        Array.Clear(Deltas);
        Array.Clear(WeightsA);
        Array.Clear(WeightsB);
        Array.Clear(SamplesA);
        Array.Clear(SamplesB);
        Array.Clear(Medians);
        Array.Clear(SlowLevels);
        Array.Clear(BitrateAccumulators);
        Array.Clear(BitrateDeltas);
        Array.Clear(ShapingAccumulators);
        Array.Clear(ShapingDeltas);
        Array.Clear(Errors);
        HasFloatInfo = false;
        FloatFlags = FloatShift = FloatMaxExponent = FloatNormalizeExponent = 0;
        Int32SentBits = Int32Zeros = Int32Ones = Int32Dups = 0;
        Bitstream = ExtraBitstream = Dsd = (0, 0);
        ExtraCrc = 0;
        ExtraPosition = ExtraHeaderLength = ExtraSize = 0;
        HasCorrection = false;
        ChannelInfo = null;
        SampleRate = null;
        HasNewConfig = false;
        QualifyMode = 0;
        LayoutChannels = 0;
        ReorderingLength = 0;
        Md5 = null;
        Md5Position = Md5HeaderLength = Md5Size = 0;
    }

    private void Process(ReadOnlySpan<byte> block, in BlockHeader header, in SubBlockSpan sub, ReadOnlySpan<byte> data, bool isMono, bool channelsAreKnown)
    {
        switch (sub.Function)
        {
            case SubBlockIds.Dummy:
                break;
            case SubBlockIds.DecorrTerms:
                ReadTerms(data, isMono);
                break;
            case SubBlockIds.DecorrWeights:
                ReadWeights(data, isMono);
                break;
            case SubBlockIds.DecorrSamples:
                ReadSamples(data, header, isMono);
                break;
            case SubBlockIds.EntropyVars:
                if (data.Length != (isMono ? 6 : 12))
                {
                    throw Refused("entropy variables", isMono ? "6 bytes for a mono block" : "12 bytes for a stereo block");
                }

                for (int i = 0; i < data.Length / 2; i++)
                {
                    Medians[i] = (uint)Exp2(data[2 * i] + (data[(2 * i) + 1] << 8));
                }

                break;
            case SubBlockIds.HybridProfile:
                ReadProfile(data, header, isMono);
                break;
            case SubBlockIds.ShapingWeights:
                ReadShaping(data, isMono);
                break;
            case SubBlockIds.FloatInfo:
                if (data.Length != 4)
                {
                    throw Refused("float info", "4 bytes");
                }

                HasFloatInfo = true;
                (FloatFlags, FloatShift, FloatMaxExponent, FloatNormalizeExponent) = (data[0], data[1], data[2], data[3]);
                break;
            case SubBlockIds.Int32Info:
                if (data.Length != 4)
                {
                    throw Refused("int32 info", "4 bytes");
                }

                (Int32SentBits, Int32Zeros, Int32Ones, Int32Dups) = (data[0], data[1], data[2], data[3]);
                break;
            case SubBlockIds.ChannelInfo:
                ReadChannelInfo(data, channelsAreKnown);
                break;
            case SubBlockIds.ChannelIdentities:
                if (data.Length == 0 || data.Contains((byte)0))
                {
                    throw new InvalidDataException("The channel identities are empty or hold a 0");
                }

                break;
            case SubBlockIds.NewConfigBlock:
                ReadNewConfig(data);
                break;
            case SubBlockIds.SampleRate:
                if (Decode.SampleRate(data) is { } rate)
                {
                    SampleRate = rate;
                }

                break;
            case SubBlockIds.WvBitstream:
                if (data.Length == 0 || (data.Length & 1) != 0)
                {
                    throw Refused("WavPack bitstream", "a bitstream of an even number of bytes, not empty");
                }

                Bitstream = (sub.DataStart, data.Length);
                break;
            case SubBlockIds.WvcBitstream:
                if (data.Length == 0 || (data.Length & 1) != 0)
                {
                    throw Refused("correction bitstream", "a bitstream of an even number of bytes, not empty");
                }

                HasCorrection = true;
                break;
            case SubBlockIds.WvxBitstream:
                if (data.Length <= 4 || (data.Length & 1) != 0)
                {
                    throw Refused("extra bitstream", "a CRC and a bitstream of an even number of bytes, more than 4");
                }

                ExtraCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
                ExtraBitstream = (sub.DataStart + 4, data.Length - 4);
                (ExtraPosition, ExtraHeaderLength, ExtraSize) = (sub.DataStart - sub.HeaderLength, sub.HeaderLength, sub.Next - sub.DataStart);
                break;
            case SubBlockIds.DsdBlock:
                ReadDsd(header, sub, data, isMono);
                break;
            case SubBlockIds.Md5Checksum or SubBlockIds.AltMd5Checksum:
                if (data.Length == 16)
                {
                    Md5 = (sub.DataStart, sub.Function == SubBlockIds.AltMd5Checksum);
                    (Md5Position, Md5HeaderLength, Md5Size) = (sub.DataStart - sub.HeaderLength, sub.HeaderLength, sub.Next - sub.DataStart);
                }

                break;
            case SubBlockIds.RiffHeader or SubBlockIds.RiffTrailer or SubBlockIds.AltHeader or SubBlockIds.AltTrailer or SubBlockIds.ConfigBlock
                or SubBlockIds.AltExtension or SubBlockIds.BlockChecksum:
                break;
            default:
                if ((sub.Function & SubBlockIds.OptionalBit) == 0)
                {
                    throw new InvalidDataException($"The block holds the sub-block with function 0x{sub.Function:X2}, which is not one WavPack defines and is not optional");
                }

                break;
        }
    }

    private static InvalidDataException Refused(string what, string expected) => new($"The {what} sub-block has a size libwavpack refuses; it takes {expected}");

    // decorr_utils.c read_decorr_terms.
    private void ReadTerms(ReadOnlySpan<byte> data, bool isMono)
    {
        if (Decode.Terms(data, isMono) is not { } terms)
        {
            throw new InvalidDataException(data.Length > Decode.MaxTerms
                ? $"The decorrelation terms are {data.Length} passes, more than the {Decode.MaxTerms} libwavpack takes"
                : "The decorrelation terms are not all valid (1 to 8, 17, 18 and, for stereo, -1 to -3)");
        }

        Passes = terms.Length;
        for (int i = 0; i < terms.Length; i++)
        {
            int pass = terms.Length - 1 - i;
            Terms[pass] = terms[i].Term;
            Deltas[pass] = terms[i].Delta;
        }
    }

    // read_decorr_weights: the weights are for the last passes first.
    private void ReadWeights(ReadOnlySpan<byte> data, bool isMono)
    {
        int count = isMono ? data.Length : data.Length / 2;
        if (count > Passes)
        {
            throw Refused("decorrelation weights", "a weight (two for stereo) for each of the passes at most");
        }

        Array.Clear(WeightsA);
        Array.Clear(WeightsB);
        int at = 0;
        for (int pass = Passes - 1; pass >= 0 && count-- > 0; pass--)
        {
            WeightsA[pass] = Decode.Weight((sbyte)data[at++]);
            if (!isMono)
            {
                WeightsB[pass] = Decode.Weight((sbyte)data[at++]);
            }
        }
    }

    // read_decorr_samples.
    private void ReadSamples(ReadOnlySpan<byte> data, in BlockHeader header, bool isMono)
    {
        Array.Clear(SamplesA);
        Array.Clear(SamplesB);
        int at = 0;
        if (header.Version == 0x402 && header.IsHybrid)
        {
            if (data.Length < (isMono ? 2 : 4))
            {
                throw Refused("decorrelation samples", "the samples of the first passes, up to all of them, ending exactly");
            }

            Errors[0] = Exp2((short)(data[0] + (data[1] << 8)));
            at += 2;
            if (!isMono)
            {
                Errors[1] = Exp2((short)(data[2] + (data[3] << 8)));
                at += 2;
            }
        }

        for (int pass = Passes - 1; pass >= 0 && at < data.Length; pass--)
        {
            int term = Terms[pass];
            int baseIndex = pass * MaxTerm;
            if (term > MaxTerm)
            {
                if (data.Length - at < (isMono ? 4 : 8))
                {
                    throw Refused("decorrelation samples", "the samples of the first passes, up to all of them, ending exactly");
                }

                SamplesA[baseIndex] = Exp2((short)(data[at] + (data[at + 1] << 8)));
                SamplesA[baseIndex + 1] = Exp2((short)(data[at + 2] + (data[at + 3] << 8)));
                at += 4;
                if (!isMono)
                {
                    SamplesB[baseIndex] = Exp2((short)(data[at] + (data[at + 1] << 8)));
                    SamplesB[baseIndex + 1] = Exp2((short)(data[at + 2] + (data[at + 3] << 8)));
                    at += 4;
                }
            }
            else if (term < 0)
            {
                if (data.Length - at < 4)
                {
                    throw Refused("decorrelation samples", "the samples of the first passes, up to all of them, ending exactly");
                }

                SamplesA[baseIndex] = Exp2((short)(data[at] + (data[at + 1] << 8)));
                SamplesB[baseIndex] = Exp2((short)(data[at + 2] + (data[at + 3] << 8)));
                at += 4;
            }
            else
            {
                for (int m = 0; m < term; m++)
                {
                    if (data.Length - at < (isMono ? 2 : 4))
                    {
                        throw Refused("decorrelation samples", "the samples of the first passes, up to all of them, ending exactly");
                    }

                    SamplesA[baseIndex + m] = Exp2((short)(data[at] + (data[at + 1] << 8)));
                    at += 2;
                    if (!isMono)
                    {
                        SamplesB[baseIndex + m] = Exp2((short)(data[at] + (data[at + 1] << 8)));
                        at += 2;
                    }
                }
            }
        }

        if (at != data.Length)
        {
            throw Refused("decorrelation samples", "the samples of the first passes, up to all of them, ending exactly");
        }
    }

    // entropy_utils.c read_hybrid_profile.
    private void ReadProfile(ReadOnlySpan<byte> data, in BlockHeader header, bool isMono)
    {
        int at = 0;
        int channels = isMono ? 1 : 2;
        if ((header.Flags & BlockHeader.HybridBitrateFlag) != 0)
        {
            if (data.Length < 2 * channels)
            {
                throw Refused("hybrid profile", "a slow level (with bitrate control), a bitrate accumulator and optionally a bitrate delta for each channel");
            }

            for (int c = 0; c < channels; c++, at += 2)
            {
                SlowLevels[c] = (uint)Exp2(data[at] + (data[at + 1] << 8));
            }
        }

        if (data.Length - at < 2 * channels)
        {
            throw Refused("hybrid profile", "a slow level (with bitrate control), a bitrate accumulator and optionally a bitrate delta for each channel");
        }

        for (int c = 0; c < channels; c++, at += 2)
        {
            BitrateAccumulators[c] = (uint)(data[at] + (data[at + 1] << 8)) << 16;
        }

        if (at < data.Length)
        {
            if (data.Length - at < 2 * channels)
            {
                throw Refused("hybrid profile", "a slow level (with bitrate control), a bitrate accumulator and optionally a bitrate delta for each channel");
            }

            for (int c = 0; c < channels; c++, at += 2)
            {
                BitrateDeltas[c] = (uint)Exp2((short)(data[at] + (data[at + 1] << 8)));
            }

            if (at < data.Length)
            {
                throw Refused("hybrid profile", "a slow level (with bitrate control), a bitrate accumulator and optionally a bitrate delta for each channel");
            }
        }
        else
        {
            BitrateDeltas[0] = BitrateDeltas[1] = 0;
        }
    }

    // decorr_utils.c read_shaping_info; the values matter only to hybrid lossless decoding with a correction file, but a
    // size libwavpack refuses refuses the block.
    private void ReadShaping(ReadOnlySpan<byte> data, bool isMono)
    {
        if (data.Length == 2)
        {
            ShapingAccumulators[0] = Decode.Weight((sbyte)data[0]) << 16;
            ShapingAccumulators[1] = Decode.Weight((sbyte)data[1]) << 16;
        }
        else if (data.Length >= (isMono ? 4 : 8))
        {
            Errors[0] = Exp2((short)(data[0] + (data[1] << 8)));
            ShapingAccumulators[0] = Exp2((short)(data[2] + (data[3] << 8)));
            int at = 4;
            if (!isMono)
            {
                Errors[1] = Exp2((short)(data[at] + (data[at + 1] << 8)));
                ShapingAccumulators[1] = Exp2((short)(data[at + 2] + (data[at + 3] << 8)));
                at += 4;
            }

            if (data.Length == (isMono ? 6 : 12))
            {
                ShapingDeltas[0] = Exp2((short)(data[at] + (data[at + 1] << 8)));
                if (!isMono)
                {
                    ShapingDeltas[1] = Exp2((short)(data[at + 2] + (data[at + 3] << 8)));
                }
            }
        }
        else
        {
            throw Refused("shaping weights", isMono ? "2 bytes, or 4 or more for a mono block" : "2 bytes, or 8 or more for a stereo block");
        }
    }

    // open_utils.c read_channel_info: the checks apply to the first channel info that gives channels.
    private void ReadChannelInfo(ReadOnlySpan<byte> data, bool channelsAreKnown)
    {
        if (data.Length is 0 or > 7)
        {
            throw Refused("channel info", "1 to 7 bytes");
        }

        if (channelsAreKnown)
        {
            return;
        }

        ChannelInfoValue? info = Decode.ChannelInfo(data);
        if (info is null)
        {
            throw new InvalidDataException("The channel info is one libwavpack refuses: no more channels than twice the streams, no more speakers in the mask than channels and, in its long form, at least as many channels as streams");
        }

        if (info.Value.Channels > 0)
        {
            ChannelInfo = info;
        }
    }

    // read_new_config_info.
    private void ReadNewConfig(ReadOnlySpan<byte> data)
    {
        HasNewConfig = true;
        QualifyMode = 0;
        LayoutChannels = 0;
        ReorderingLength = 0;
        if (data.Length < 2)
        {
            return;
        }

        QualifyMode = data[1];
        if (data.Length >= 3)
        {
            if (data.Length >= 4)
            {
                int count = data[3];
                LayoutChannels = count;
                if (data.Length > 4)
                {
                    int given = data.Length - 4;
                    if (given > count)
                    {
                        throw Refused("new config block", $"at most {4 + count} bytes, as its layout has {count} channels to reorder");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        byte value = i < given ? data[4 + i] : (byte)i;
                        Reordering[i] = value >= count ? (byte)0 : value;
                    }

                    ReorderingLength = count;
                }
            }
        }
    }

    // dsd: init_dsd_block of unpack_dsd.c checks the block's size and mode and the lengths the fast and high modes need; the values
    // of the fast mode's probability tables are checked as the DSD decoder reads them (a table that is not valid is a block it refuses).
    private void ReadDsd(in BlockHeader header, in SubBlockSpan sub, ReadOnlySpan<byte> data, bool isMono)
    {
        if (data.Length < 2 || data[0] > 31)
        {
            throw new InvalidDataException("The DSD block is one libwavpack refuses: it needs 2 bytes and a rate power of at most 31");
        }

        int mode = data[1];
        if (mode == 0)
        {
            if (data.Length - 2 != (long)header.Samples * (isMono ? 1 : 2))
            {
                throw new InvalidDataException("The DSD block in the stored mode does not hold the bytes the block counts");
            }
        }
        else if (!Decode.IsDsdModeValid(mode))
        {
            throw new InvalidDataException($"The DSD block has the mode {mode}, which is not 0, 1 or 3");
        }
        else if (mode == 1 && (data.Length < 3 || data[2] > 5 || data.Length == 3))
        {
            throw new InvalidDataException("The DSD block in the fast mode has no history bits, more than 5 of them, or no probabilities");
        }
        else if (mode == 3 && (data.Length - 2 < (isMono ? 13 : 20) || data[3] != 20))
        {
            throw new InvalidDataException("The DSD block in the high mode is shorter than its tables or has a rate step other than 20");
        }

        Dsd = (sub.DataStart, data.Length);
    }

    // entropy_utils.c wp_exp2s: the number a logarithm of 8 bits of precision stands for.
    public static int Exp2(int log)
    {
        if (log < 0)
        {
            return -Exp2(-log);
        }

        uint value = (uint)Exp2Table[log & 0xFF] | 0x100;
        int shift = log >> 8;
        return shift <= 9 ? (int)(value >> (9 - shift)) : (int)(value << ((shift - 9) & 0x1F));
    }

    private static readonly byte[] Exp2Table =
    [
        0x00, 0x01, 0x01, 0x02, 0x03, 0x03, 0x04, 0x05, 0x06, 0x06, 0x07, 0x08, 0x08, 0x09, 0x0a, 0x0b,
        0x0b, 0x0c, 0x0d, 0x0e, 0x0e, 0x0f, 0x10, 0x10, 0x11, 0x12, 0x13, 0x13, 0x14, 0x15, 0x16, 0x16,
        0x17, 0x18, 0x19, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1d, 0x1e, 0x1f, 0x20, 0x20, 0x21, 0x22, 0x23,
        0x24, 0x24, 0x25, 0x26, 0x27, 0x28, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2c, 0x2d, 0x2e, 0x2f, 0x30,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3a, 0x3b, 0x3c, 0x3d,
        0x3e, 0x3f, 0x40, 0x41, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x48, 0x49, 0x4a, 0x4b,
        0x4c, 0x4d, 0x4e, 0x4f, 0x50, 0x51, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a,
        0x5b, 0x5c, 0x5d, 0x5e, 0x5e, 0x5f, 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7a, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x87, 0x88, 0x89, 0x8a,
        0x8b, 0x8c, 0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x92, 0x93, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0x9b,
        0x9c, 0x9d, 0x9f, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xad,
        0xaf, 0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xbc, 0xbd, 0xbe, 0xbf, 0xc0,
        0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc8, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf, 0xd0, 0xd2, 0xd3, 0xd4,
        0xd6, 0xd7, 0xd8, 0xd9, 0xdb, 0xdc, 0xdd, 0xde, 0xe0, 0xe1, 0xe2, 0xe4, 0xe5, 0xe6, 0xe8, 0xe9,
        0xea, 0xec, 0xed, 0xee, 0xf0, 0xf1, 0xf2, 0xf4, 0xf5, 0xf6, 0xf8, 0xf9, 0xfa, 0xfc, 0xfd, 0xff,
    ];
}

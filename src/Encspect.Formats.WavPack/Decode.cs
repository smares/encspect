using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.WavPack;

// The sub-blocks whose contents the analyzer decodes, as libwavpack 5.6.0 reads them (open_utils.c,
// decorr_utils.c, entropy_utils.c, unpack_dsd.c). The functions return null or false where
// libwavpack refuses the sub-block; the reports tell why.
internal static class Decode
{
    public const int MaxTerms = 16;
    public const int MaxTerm = 8;

    // libwavpack's default number of streams when a channel info sub-block gives none (OLD_MAX_STREAMS).
    private const int OldMaxStreams = 8;

    // The decorrelation passes of ID_DECORR_TERMS: a byte per pass, the term in its low five bits
    // less 5 and the delta in the high three. Valid terms are 1 to 8, 17, 18 and, for stereo, -1 to -3
    // (read_decorr_terms). Null where libwavpack refuses them.
    public static (int Term, int Delta)[]? Terms(ReadOnlySpan<byte> data, bool isMono)
    {
        if (data.Length > MaxTerms)
        {
            return null;
        }

        var terms = new (int, int)[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int term = (data[i] & 0x1F) - 5;
            if (!IsValidTerm(term, isMono))
            {
                return null;
            }

            terms[i] = (term, data[i] >> 5);
        }

        return terms;
    }

    public static bool IsValidTerm(int term, bool isMono) =>
        term != 0 && term >= -3 && (term <= MaxTerm || term is 17 or 18) && !(isMono && term < 0);

    // A decorrelation weight as the sub-block stores it, back to the 12-bit range (restore_weight).
    public static int Weight(sbyte stored)
    {
        int result = stored * 8;
        return result > 0 ? result + ((result + 64) >> 7) : result;
    }

    // How many bytes of ID_DECORR_SAMPLES the pass with this term takes: 2 samples (4 for stereo)
    // for terms above 8, a sample of each channel for the negative terms, term samples (of each
    // channel) otherwise (read_decorr_samples).
    public static int SamplesOf(int term, bool isMono) => term > MaxTerm ? (isMono ? 4 : 8) : term < 0 ? 4 : term * (isMono ? 2 : 4);

    // How many of the first passes in the order of the terms the sub-block's data covers, or -1 where
    // it ends inside a pass or past them all (libwavpack stops reading when the data ends and requires
    // it to end exactly; the encoder sends the samples of the first pass only).
    public static int PassesCovered((int Term, int Delta)[] terms, int length, bool isMono, bool skipsErrors)
    {
        int used = skipsErrors ? (isMono ? 2 : 4) : 0;
        if (length < used)
        {
            return -1;
        }

        int covered = 0;
        for (int i = 0; i < terms.Length && used < length; i++)
        {
            used += SamplesOf(terms[i].Term, isMono);
            covered++;
        }

        return used == length ? covered : -1;
    }

    public static ChannelInfoValue? ChannelInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > 7)
        {
            return null;
        }

        int channels;
        int streams;
        uint mask = 0;
        if (data.Length >= 6)
        {
            channels = (data[0] | ((data[2] & 0xF) << 8)) + 1;
            streams = (data[1] | ((data[2] & 0xF0) << 4)) + 1;
            if (channels < streams)
            {
                return null;
            }

            for (int i = 3; i < data.Length; i++)
            {
                mask |= (uint)data[i] << (8 * (i - 3));
            }
        }
        else
        {
            channels = data[0];
            streams = OldMaxStreams;
            for (int i = 1; i < data.Length; i++)
            {
                mask |= (uint)data[i] << (8 * (i - 1));
            }
        }

        if (channels == 0 || channels > streams * 2 || System.Numerics.BitOperations.PopCount(mask) > channels)
        {
            return null;
        }

        return new ChannelInfoValue(channels, streams, mask, data.Length);
    }

    // The mask's speakers in the order of its bits, which is the order of the channels in the file
    // (WAVEFORMATEXTENSIBLE's), then channels without a position up to the count. Positions above
    // the 18 the WAVE format names are channels without one too.
    public static List<Speaker> Layout(uint mask, int channels)
    {
        var layout = new List<Speaker>();
        for (int bit = 0; bit < 32 && layout.Count < channels; bit++)
        {
            if ((mask & (1u << bit)) != 0)
            {
                layout.Add(bit <= (int)Speaker.TopBackRight - 1 ? (Speaker)(bit + 1) : Speaker.Discrete);
            }
        }

        while (layout.Count < channels)
        {
            layout.Add(Speaker.Discrete);
        }

        return layout;
    }

    // ID_CONFIG_BLOCK: the config flags from bit 8 up in three bytes, then the level of extra
    // processing where the flags say it was used (read_config_info). Null for fewer than 3 bytes,
    // which libwavpack skips.
    public static ConfigValue? Config(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return null;
        }

        uint flags = ((uint)data[0] << 8) | ((uint)data[1] << 16) | ((uint)data[2] << 24);
        return new ConfigValue(flags, data.Length > 3 && (flags & ConfigExtraMode) != 0 ? data[3] : null);
    }

    public const uint ConfigFastMode = 0x200;
    public const uint ConfigHighMode = 0x800;
    public const uint ConfigVeryHighMode = 0x1000;
    public const uint ConfigBitrateInKbps = 0x2000;
    public const uint ConfigAutoShaping = 0x4000;
    public const uint ConfigShapeOverride = 0x8000;
    public const uint ConfigJointOverride = 0x10000;
    public const uint ConfigDynamicShaping = 0x20000;
    public const uint ConfigCreateExe = 0x40000;
    public const uint ConfigCreateWvc = 0x80000;
    public const uint ConfigOptimizeWvc = 0x100000;
    public const uint ConfigCalcNoise = 0x800000;
    public const uint ConfigExtraMode = 0x2000000;
    public const uint ConfigSkipWvx = 0x4000000;
    public const uint ConfigMd5 = 0x8000000;
    public const uint ConfigMergeBlocks = 0x10000000;
    public const uint ConfigPairUndefinedChannels = 0x20000000;
    public const uint ConfigOptimizeMono = 0x80000000;

    // ID_NEW_CONFIG_BLOCK: the source file's format, the flags of what the encoder did to the source
    // audio, and a Core Audio layout (read_new_config_info). Null for an empty one.
    public static NewConfigValue? NewConfig(ReadOnlySpan<byte> data, int channels)
    {
        if (data.Length < 2)
        {
            return null;
        }

        int tag = 0;
        int layoutChannels = 0;
        if (data.Length >= 3)
        {
            tag = data[2];
            layoutChannels = data.Length >= 4 ? data[3] : channels;
        }

        return new NewConfigValue(data[0], data[1], tag, layoutChannels);
    }

    public static string FileFormat(int format) => format switch
    {
        0 => "WAV",
        1 => "Wave64",
        2 => "CAF",
        3 => "DSDIFF",
        4 => "DSF",
        5 => "AIFF",
        _ => $"unknown ({format})",
    };

    // The sample rate of ID_SAMPLE_RATE: 3 bytes, or 4 with the high bit of the last reserved.
    public static int? SampleRate(ReadOnlySpan<byte> data) => data.Length switch
    {
        3 => data[0] | (data[1] << 8) | (data[2] << 16),
        4 => data[0] | (data[1] << 8) | (data[2] << 16) | ((data[3] & 0x7F) << 24),
        _ => null,
    };

    public static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(data);

    // The block checksum: -1 over the 16-bit little-endian words before the sub-block, each step
    // times 3 plus the word; four stored bytes, or two of the sum folded onto itself
    // (WavpackVerifySingleBlock).
    public static uint Checksum(ReadOnlySpan<byte> block, int length)
    {
        uint sum = uint.MaxValue;
        for (int i = 0; i + 1 < length; i += 2)
        {
            sum = (sum * 3) + BinaryPrimitives.ReadUInt16LittleEndian(block[i..]);
        }

        return sum;
    }

    public static bool ChecksumMatches(uint sum, ReadOnlySpan<byte> stored)
    {
        if (stored.Length == 4)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(stored) == sum;
        }

        sum ^= sum >> 16;
        return stored.Length == 2 && BinaryPrimitives.ReadUInt16LittleEndian(stored) == (ushort)sum;
    }

    public static uint StoredChecksum(ReadOnlySpan<byte> stored, uint sum)
    {
        if (stored.Length == 4)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(stored);
        }

        return stored.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(stored) : 0u;
    }

    // The target of a hybrid block in bits per sample and channel, from its profile: the encoder
    // stores the target less 568/256 as the bitrate accumulator, per channel; with the noise
    // balanced between channels (or mono) the first channel's holds it (write_words.c
    // word_set_bitrate). A false stereo block's accumulator holds twice the target less 512/256.
    public static double? HybridTarget(BlockHeader header, ReadOnlySpan<byte> profile)
    {
        if ((header.Flags & BlockHeader.HybridBitrateFlag) == 0 || !(header.IsMonoData || (header.Flags & BlockHeader.HybridBalanceFlag) != 0))
        {
            return null;
        }

        int channels = header.IsMonoData ? 1 : 2;
        int offset = 2 * channels;
        if (profile.Length < offset + 2)
        {
            return null;
        }

        int accumulator = BinaryPrimitives.ReadUInt16LittleEndian(profile[offset..]);
        double bits = header.IsFalseStereo ? (accumulator + 568 + 512) / 2.0 : accumulator + 568;
        return bits / 256;
    }

    public static string DsdMode(int mode) => mode switch
    {
        0 => "stored",
        1 => "fast",
        3 => "high",
        _ => $"unknown ({mode})",
    };

    public static bool IsDsdModeValid(int mode) => mode is 0 or 1 or 3;
}

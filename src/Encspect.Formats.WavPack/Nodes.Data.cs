using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.WavPack;

// What the decoded sub-blocks hold. Only the first bytes of a long sub-block are kept, so every
// reader checks how many there are.
internal static partial class Nodes
{
    private const int MaxListedItems = 64;

    private static void DataChildren(State s, Entry e, Detail detail, SubBlock sb, ReadOnlySpan<byte> kept, long at, Node data)
    {
        BlockHeader h = e.Header;
        bool isMono = h.IsMonoData;
        switch (sb.Function)
        {
            case SubBlockIds.DecorrTerms:
                Terms(kept, isMono, at, data);
                break;
            case SubBlockIds.DecorrWeights:
                Weights(kept, h, at, data);
                break;
            case SubBlockIds.DecorrSamples:
                SamplesOfPasses(detail, kept, h, at, data);
                break;
            case SubBlockIds.EntropyVars:
                Entropy(kept, h, at, data);
                break;
            case SubBlockIds.HybridProfile:
                Profile(kept, h, at, data);
                break;
            case SubBlockIds.ShapingWeights:
                Shaping(kept, h, at, data);
                break;
            case SubBlockIds.FloatInfo when kept.Length == 4:
                FloatInfo(kept, at, data);
                break;
            case SubBlockIds.Int32Info when kept.Length == 4:
                data.Add(Field(at, 1, "Sent Bits", NodeValue.Integer(kept[0], "bits", "bit"), Explain.Int32SentBits, Topics.FloatAndLargeIntegers));
                data.Add(Field(at + 1, 1, "Zero Bits", NodeValue.Integer(kept[1], "bits", "bit"), Explain.Int32Zeros, Topics.FloatAndLargeIntegers));
                data.Add(Field(at + 2, 1, "One Bits", NodeValue.Integer(kept[2], "bits", "bit"), Explain.Int32Ones, Topics.FloatAndLargeIntegers));
                data.Add(Field(at + 3, 1, "Duplicated Bits", NodeValue.Integer(kept[3], "bits", "bit"), Explain.Int32Dups, Topics.FloatAndLargeIntegers));
                break;
            case SubBlockIds.WvxBitstream when kept.Length >= 4:
                data.Add(Field(at, 4, "CRC", NodeValue.Hex(BinaryPrimitives.ReadUInt32LittleEndian(kept), 8), Explain.WvxCrc, Topics.Crc));
                data.Add(Field(at + 4, sb.DataLength - 4, "Bitstream", NodeValue.Integer(sb.DataLength - 4, "bytes", "byte"), Explain.Bitstream, Topics.FloatAndLargeIntegers));
                break;
            case SubBlockIds.WvBitstream or SubBlockIds.WvcBitstream:
                data.Add(Field(at, sb.DataLength, "Bitstream", NodeValue.Integer(sb.DataLength, "bytes", "byte"), Explain.Bitstream, Topics.Decorrelation));
                break;
            case SubBlockIds.ChannelInfo:
                ChannelInfo(kept, at, data);
                break;
            case SubBlockIds.DsdBlock when kept.Length >= 2:
                Dsd(kept, sb, at, data);
                break;
            case SubBlockIds.RiffHeader:
                Riff(kept, sb.DataLength, at, data);
                break;
            case SubBlockIds.RiffTrailer:
                Chunks(kept, sb.DataLength, at, data, 0);
                break;
            case SubBlockIds.AltHeader or SubBlockIds.AltTrailer:
                AlternateWrapper(kept, sb.DataLength, at, data);
                break;
            case SubBlockIds.ConfigBlock:
                Config(kept, sb.DataLength, at, data);
                break;
            case SubBlockIds.Md5Checksum or SubBlockIds.AltMd5Checksum when kept.Length == 16:
                data.Add(Field(at, 16, "Digest", Decode.Hex(kept), Explain.Md5Digest, Topics.Wrapper));
                break;
            case SubBlockIds.SampleRate when Decode.SampleRate(kept) is { } rate:
                data.Add(Field(at, kept.Length, "Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.SampleRateField));
                break;
            case SubBlockIds.AltExtension:
                data.Add(Field(at, sb.DataLength, "Extension", Encoding.ASCII.GetString(kept).TrimEnd('\0'), Explain.Extension, Topics.Wrapper));
                break;
            case SubBlockIds.NewConfigBlock:
                NewConfig(kept, sb.DataLength, h, s, at, data);
                break;
            case SubBlockIds.ChannelIdentities:
                Identities(kept, sb.DataLength, at, data);
                break;
            case SubBlockIds.BlockChecksum:
                Checksum(detail, kept, sb, at, data);
                break;
            case SubBlockIds.Dummy:
                data.Add(Field(at, sb.DataLength, "Padding", NodeValue.Integer(sb.DataLength, "bytes", "byte"), Explain.DummyBytes));
                break;
        }
    }

    private static void Terms(ReadOnlySpan<byte> kept, bool isMono, long at, Node data)
    {
        for (int i = 0; i < kept.Length && i < MaxListedItems; i++)
        {
            int term = (kept[i] & 0x1F) - 5;
            int delta = kept[i] >> 5;
            var pass = new Node($"Pass {i + 1}", at + i, 1, $"term {term}, delta {delta}") { Description = Explain.Pass, Topic = Topics.Decorrelation };
            pass.Add(Node.FromBits("Term", at + i, 0, 5, NodeValue.Integer(term), NodeKind.Detail, Decode.IsValidTerm(term, isMono) ? Explain.TermOf(term) : Explain.TermInvalid, Topics.Decorrelation, BitOrder.LeastSignificantFirst));
            pass.Add(Node.FromBits("Delta", at + i, 5, 8, NodeValue.Integer(delta), NodeKind.Detail, Explain.Delta, Topics.Decorrelation, BitOrder.LeastSignificantFirst));
            data.Add(pass);
        }
    }

    // What the two channels of a stereo block are called where the encoder codes them: the first as
    // left and the second as right, or with joint stereo as side and mid (pack.c, "left -= right"
    // and "right += left >> 1"). The noise shaping is applied before that conversion, so its values
    // stay left and right.
    private static (string First, string Second) PairNames(BlockHeader h) =>
        (h.Flags & BlockHeader.JointStereoFlag) != 0 ? ("Side", "Mid") : ("Left", "Right");

    private static void Weights(ReadOnlySpan<byte> kept, BlockHeader h, long at, Node data)
    {
        bool isMono = h.IsMonoData;
        (string first, string second) = PairNames(h);
        int step = isMono ? 1 : 2;
        for (int i = 0; i + step <= kept.Length && i / step < MaxListedItems; i += step)
        {
            var pass = new Node($"Pass {(i / step) + 1}", at + i, step) { Description = Explain.PassWeights, Topic = Topics.Decorrelation };
            pass.Add(Field(at + i, 1, isMono ? "Weight" : $"{first} Weight", NodeValue.Integer(Decode.Weight((sbyte)kept[i])), Explain.Weight, Topics.Decorrelation));
            if (!isMono)
            {
                pass.Add(Field(at + i + 1, 1, $"{second} Weight", NodeValue.Integer(Decode.Weight((sbyte)kept[i + 1])), Explain.Weight, Topics.Decorrelation));
            }

            data.Add(pass);
        }
    }

    private static void SamplesOfPasses(Detail detail, ReadOnlySpan<byte> kept, BlockHeader h, long at, Node data)
    {
        (int Term, int Delta)[]? terms = detail.TermsIndex >= 0 && detail.TermsIndex < detail.SubBlocks.Count
            ? Decode.Terms(detail.Data.AsSpan(detail.SubBlocks[detail.TermsIndex].Stored, detail.SubBlocks[detail.TermsIndex].StoredLength), h.IsMonoData)
            : null;
        if (terms is null)
        {
            return;
        }

        int position = h.Version == 0x402 && h.IsHybrid ? (h.IsMonoData ? 2 : 4) : 0;
        for (int i = 0; i < terms.Length && position < kept.Length && i < MaxListedItems; i++)
        {
            int size = Decode.SamplesOf(terms[i].Term, h.IsMonoData);
            data.Add(Field(at + position, Math.Min(size, kept.Length - position), $"Pass {i + 1}", NodeValue.Integer(size / 2, "values", "value"), Explain.PassSamples, Topics.Decorrelation));
            position += size;
        }
    }

    private static void Entropy(ReadOnlySpan<byte> kept, BlockHeader h, long at, Node data)
    {
        bool isMono = h.IsMonoData;
        (string first, string second) = PairNames(h);
        int channels = isMono ? 1 : 2;
        for (int c = 0; c < channels && kept.Length >= (c + 1) * 6; c++)
        {
            var channel = new Node(isMono ? "Medians" : $"{(c == 0 ? first : second)} Medians", at + (c * 6), 6) { Description = Explain.Medians, Topic = Topics.Decorrelation };
            for (int m = 0; m < 3; m++)
            {
                channel.Add(Field(at + (c * 6) + (m * 2), 2, $"Median {m + 1}", NodeValue.Integer(ReadUInt16(kept, (c * 6) + (m * 2))), Explain.Median, Topics.Decorrelation));
            }

            data.Add(channel);
        }
    }

    private static void Profile(ReadOnlySpan<byte> kept, BlockHeader h, long at, Node data)
    {
        int channels = h.IsMonoData ? 1 : 2;
        (string first, string second) = PairNames(h);
        int position = 0;
        if ((h.Flags & BlockHeader.HybridBitrateFlag) != 0)
        {
            for (int c = 0; c < channels && position + 2 <= kept.Length; c++, position += 2)
            {
                data.Add(Field(at + position, 2, channels == 1 ? "Slow Level" : $"{(c == 0 ? first : second)} Slow Level", NodeValue.Integer(ReadUInt16(kept, position)), Explain.SlowLevel, Topics.Hybrid));
            }
        }

        for (int c = 0; c < channels && position + 2 <= kept.Length; c++, position += 2)
        {
            data.Add(Field(at + position, 2, channels == 1 ? "Bitrate Accumulator" : $"{(c == 0 ? first : second)} Bitrate Accumulator", NodeValue.Integer(ReadUInt16(kept, position)), Explain.BitrateAccumulator, Topics.Hybrid));
        }

        for (int c = 0; c < channels && position + 2 <= kept.Length; c++, position += 2)
        {
            data.Add(Field(at + position, 2, channels == 1 ? "Bitrate Delta" : $"{(c == 0 ? first : second)} Bitrate Delta", NodeValue.Integer((short)ReadUInt16(kept, position)), Explain.BitrateDelta, Topics.Hybrid));
        }

        if (Decode.HybridTarget(h, kept) is { } target)
        {
            data.Add(Field(at, kept.Length, "Target Bitrate", Summary.TargetValue(target.Bits, 2, "bits/sample", target.IsAtMost), target.IsAtMost ? Explain.TargetBitrateAtMost : Explain.TargetBitrate, Topics.Hybrid));
        }
    }

    private static void Shaping(ReadOnlySpan<byte> kept, BlockHeader h, long at, Node data)
    {
        bool isMono = h.IsMonoData;
        if (kept.Length == 2)
        {
            data.Add(Field(at, 1, isMono ? "Weight" : "Left Weight", NodeValue.Integer(Decode.Weight((sbyte)kept[0])), Explain.ShapingWeight, Topics.Hybrid));
            data.Add(isMono
                ? Field(at + 1, 1, "Unused Weight", NodeValue.Integer(Decode.Weight((sbyte)kept[1])), Explain.ShapingWeightUnused, Topics.Hybrid)
                : Field(at + 1, 1, "Right Weight", NodeValue.Integer(Decode.Weight((sbyte)kept[1])), Explain.ShapingWeight, Topics.Hybrid));
            return;
        }

        int channels = isMono ? 1 : 2;
        int position = 0;
        for (int c = 0; c < channels && position + 4 <= kept.Length; c++, position += 4)
        {
            string side = isMono ? string.Empty : c == 0 ? "Left " : "Right ";
            data.Add(Field(at + position, 2, side + "Error", NodeValue.Integer((short)ReadUInt16(kept, position)), Explain.ShapingError, Topics.Hybrid));
            data.Add(Field(at + position + 2, 2, side + "Shaping Accumulator", NodeValue.Integer((short)ReadUInt16(kept, position + 2)), Explain.ShapingAccumulator, Topics.Hybrid));
        }

        for (int c = 0; c < channels && position + 2 <= kept.Length; c++, position += 2)
        {
            string side = isMono ? string.Empty : c == 0 ? "Left " : "Right ";
            data.Add(Field(at + position, 2, side + "Shaping Delta", NodeValue.Integer((short)ReadUInt16(kept, position)), Explain.ShapingDelta, Topics.Hybrid));
        }
    }

    private static void FloatInfo(ReadOnlySpan<byte> kept, long at, Node data)
    {
        var flags = new Node("Flags", at, 1, NodeValue.Hex(kept[0], 2)) { Description = Explain.FloatFlags, Topic = Topics.FloatAndLargeIntegers };
        (string Name, string Description)[] bits =
        [
            ("HasShiftedOnes", Explain.FloatShiftOnes), ("HasShiftedSameBits", Explain.FloatShiftSame), ("HasShiftedBitsSent", Explain.FloatShiftSent),
            ("HasZerosSent", Explain.FloatZerosSent), ("HasNegativeZeros", Explain.FloatNegativeZeros), ("HasExceptions", Explain.FloatExceptions),
        ];
        for (int i = 0; i < bits.Length; i++)
        {
            flags.Add(Node.FromBits(bits[i].Name, at, i, i + 1, NodeValue.Boolean((kept[0] & (1 << i)) != 0), NodeKind.Detail, bits[i].Description, Topics.FloatAndLargeIntegers, BitOrder.LeastSignificantFirst));
        }

        data.Add(flags);
        data.Add(Field(at + 1, 1, "Shift", NodeValue.Integer(kept[1], "bits", "bit"), Explain.FloatShift, Topics.FloatAndLargeIntegers));
        data.Add(Field(at + 2, 1, "Maximum Exponent", NodeValue.Integer(kept[2]), Explain.FloatMaxExponent, Topics.FloatAndLargeIntegers));
        data.Add(Field(at + 3, 1, "Normalization Exponent", NodeValue.Integer(kept[3]), Explain.FloatNormExponent, Topics.FloatAndLargeIntegers));
    }

    private static void ChannelInfo(ReadOnlySpan<byte> kept, long at, Node data)
    {
        if (Decode.ChannelInfo(kept) is not { } info)
        {
            return;
        }

        if (info.Length >= 6)
        {
            data.Add(new Node("Channel Count", at, 3, NodeValue.Integer(info.Channels, "channels", "channel")) { Ranges = [new ByteRange(at, 1), new ByteRange(at + 2, 1)], Description = Explain.ChannelCount, Topic = Topics.Channels });
            data.Add(new Node("Stream Count", at + 1, 2, NodeValue.Integer(info.Streams, "streams", "stream")) { Description = Explain.StreamCount, Topic = Topics.Blocks });
            data.Add(Field(at + 3, info.Length - 3, "Channel Mask", NodeValue.Hex(info.Mask, 2 * (info.Length - 3)), Explain.ChannelMask, Topics.Channels));
        }
        else
        {
            data.Add(Field(at, 1, "Channel Count", NodeValue.Integer(info.Channels, "channels", "channel"), Explain.ChannelCount, Topics.Channels));
            if (info.Length > 1)
            {
                data.Add(Field(at + 1, info.Length - 1, "Channel Mask", NodeValue.Hex(info.Mask, 2 * (info.Length - 1)), Explain.ChannelMask, Topics.Channels));
            }
        }

        if (info.Mask != 0)
        {
            data.Add(Field(at, info.Length, "Channel Layout", ChannelLayouts.Describe(Decode.Layout(info.Mask, info.Channels)), Explain.ChannelLayoutField, Topics.Channels));
        }
    }

    private static void Dsd(ReadOnlySpan<byte> kept, SubBlock sb, long at, Node data)
    {
        data.Add(Field(at, 1, "Rate Multiplier", kept[0] <= 31 ? NodeValue.Integer(1L << kept[0]) : "invalid", Explain.DsdMultiplier, Topics.Dsd));
        data.Add(Field(at + 1, 1, "Mode", Decode.DsdMode(kept[1]), Explain.DsdMode, Topics.Dsd));
        if (kept[1] == 1 && kept.Length >= 3)
        {
            data.Add(Field(at + 2, 1, "History Bits", NodeValue.Integer(kept[2], "bits", "bit"), Explain.DsdHistoryBits, Topics.Dsd));
        }

        int offset = kept[1] == 1 ? 3 : 2;
        if (sb.DataLength > offset)
        {
            data.Add(Field(at + offset, sb.DataLength - offset, "Coded Data", NodeValue.Integer(sb.DataLength - offset, "bytes", "byte"), Explain.DsdData, Topics.Dsd));
        }
    }

    private static void Config(ReadOnlySpan<byte> kept, int length, long at, Node data)
    {
        if (Decode.Config(kept) is not { } config)
        {
            return;
        }

        var flags = new Node("Flags", at, 3, NodeValue.Hex(config.Flags, 8)) { Description = Explain.ConfigFlags, Topic = Topics.Blocks };
        (uint Bit, string Name, string Description, string? Topic)[] bits =
        [
            (Decode.ConfigFastMode, "IsFastMode", Explain.ConfigFast, null),
            (Decode.ConfigHighMode, "IsHighMode", Explain.ConfigHigh, null),
            (Decode.ConfigVeryHighMode, "IsVeryHighMode", Explain.ConfigVeryHigh, null),
            (Decode.ConfigBitrateInKbps, "IsBitrateInKbps", Explain.ConfigKbps, Topics.Hybrid),
            (Decode.ConfigAutoShaping, "UsesAutomaticShaping", Explain.ConfigAutoShaping, Topics.Hybrid),
            (Decode.ConfigShapeOverride, "IsShapingOverridden", Explain.ConfigShapeOverride, Topics.Hybrid),
            (Decode.ConfigJointOverride, "IsJointStereoOverridden", Explain.ConfigJointOverride, Topics.MidSideStereo),
            (Decode.ConfigDynamicShaping, "UsesDynamicNoiseShaping", Explain.ConfigDynamicShaping, Topics.Hybrid),
            (Decode.ConfigCreateExe, "IsSelfExtracting", Explain.ConfigExe, null),
            (Decode.ConfigCreateWvc, "CreatesCorrectionFile", Explain.ConfigCreateWvc, Topics.Hybrid),
            (Decode.ConfigOptimizeWvc, "OptimizesCorrectionFile", Explain.ConfigOptimizeWvc, Topics.Hybrid),
            (Decode.ConfigCalcNoise, "CalculatesNoise", Explain.ConfigCalcNoise, Topics.Hybrid),
            (Decode.ConfigExtraMode, "UsesExtraProcessing", Explain.ConfigExtra, Topics.Decorrelation),
            (Decode.ConfigSkipWvx, "SkipsExtraBitstream", Explain.ConfigSkipWvx, Topics.FloatAndLargeIntegers),
            (Decode.ConfigMd5, "StoresMd5", Explain.ConfigMd5, Topics.Wrapper),
            (Decode.ConfigMergeBlocks, "MergesBlocks", Explain.ConfigMerge, null),
            (Decode.ConfigPairUndefinedChannels, "PairsUnassignedChannels", Explain.ConfigPair, Topics.Channels),
            (Decode.ConfigOptimizeMono, "OptimizesMono", Explain.ConfigMono, null),
        ];
        uint known = 0;
        foreach ((uint bit, string name, string description, string? topic) in bits)
        {
            int position = System.Numerics.BitOperations.TrailingZeroCount(bit) - 8;
            known |= bit;
            flags.Add(Node.FromBits(name, at, position, position + 1, NodeValue.Boolean((config.Flags & bit) != 0), NodeKind.Detail, description, topic, BitOrder.LeastSignificantFirst));
        }

        data.Add(flags);
        uint other = config.Flags & ~known & 0xFFFFFF00;
        if (other != 0)
        {
            data.Add(Field(at, 3, "Other Flags", NodeValue.Hex(other, 8), Explain.ConfigOther));
        }

        if (config.ExtraLevel is { } level)
        {
            data.Add(Field(at + 3, 1, "Extra Processing Level", NodeValue.Integer(level), Explain.ConfigExtraLevel, Topics.Decorrelation));
        }

        int rest = length - (config.ExtraLevel is null ? 3 : 4);
        if (rest > 0)
        {
            data.Add(Field(at + length - rest, rest, "Further Bytes", NodeValue.Integer(rest, "bytes", "byte"), Explain.ConfigFurther));
        }
    }

    private static void NewConfig(ReadOnlySpan<byte> kept, int length, BlockHeader h, State s, long at, Node data)
    {
        int channels = s.ChannelInfo?.Value.Channels ?? (h.IsMono ? 1 : 2);
        if (Decode.NewConfig(kept, channels) is not { } config)
        {
            return;
        }

        data.Add(Field(at, 1, "File Format", Decode.FileFormat(config.FileFormat), Explain.FileFormat, Topics.Wrapper));
        var flags = new Node("Quality Flags", at + 1, 1, NodeValue.Hex(config.QualityFlags, 2)) { Description = Explain.QualityFlags, Topic = Topics.Wrapper };
        (string Name, string Description)[] bits =
        [
            ("IsBigEndian", Explain.QmodeBigEndian), ("HasSignedBytes", Explain.QmodeSignedBytes), ("HasUnsignedWords", Explain.QmodeUnsignedWords),
            ("HasReorderedChannels", Explain.QmodeReordered), ("IsDsdLeastSignificantBitFirst", Explain.QmodeDsdLsb),
            ("IsDsdMostSignificantBitFirst", Explain.QmodeDsdMsb), ("IsDsdInBlocks", Explain.QmodeDsdBlocks),
        ];
        for (int i = 0; i < bits.Length; i++)
        {
            flags.Add(Node.FromBits(bits[i].Name, at + 1, i, i + 1, NodeValue.Boolean((config.QualityFlags & (1 << i)) != 0), NodeKind.Detail, bits[i].Description, Topics.Wrapper, BitOrder.LeastSignificantFirst));
        }

        data.Add(flags);
        if (length >= 3)
        {
            data.Add(Field(at + 2, 1, "Channel Layout Tag", NodeValue.Hex(config.LayoutTag, 2), Explain.LayoutTag, Topics.Channels));
        }

        if (length >= 4)
        {
            data.Add(Field(at + 3, 1, "Channels In Layout", NodeValue.Integer(config.LayoutChannels, "channels", "channel"), Explain.LayoutChannels, Topics.Channels));
        }

        if (length > 4)
        {
            data.Add(Field(at + 4, length - 4, "Channel Reordering", Decode.Hex(kept[4..Math.Min(kept.Length, length)]), Explain.Reordering, Topics.Channels));
        }
    }

    private static void Identities(ReadOnlySpan<byte> kept, int length, long at, Node data)
    {
        for (int i = 0; i < kept.Length && i < MaxListedItems; i++)
        {
            byte id = kept[i];
            string value = id == 0xFF ? "unassigned" : id is >= 1 and <= (int)Speaker.TopBackRight ? ChannelLayouts.Name((Speaker)id) : id == 0 ? "invalid" : $"position {id}";
            data.Add(Field(at + i, 1, $"Channel {i + 1}", value, Explain.Identity, Topics.Channels));
        }

        if (length > MaxListedItems)
        {
            data.Add(Field(at + MaxListedItems, length - MaxListedItems, "Further Channels", NodeValue.Integer(length - MaxListedItems, "channels", "channel"), Explain.FurtherItems));
        }
    }

    private static void Checksum(Detail detail, ReadOnlySpan<byte> kept, SubBlock sb, long at, Node data)
    {
        if (kept.Length is not (2 or 4) || sb.IsOdd)
        {
            return;
        }

        bool isValid = Decode.ChecksumMatches(detail.Computed, kept);
        data.Add(Field(at, kept.Length, "Stored Checksum", NodeValue.Hex(Decode.StoredChecksum(kept, detail.Computed), 2 * kept.Length), Explain.StoredChecksum, Topics.Crc));
        uint computed = kept.Length == 4 ? detail.Computed : (ushort)(detail.Computed ^ (detail.Computed >> 16));
        data.Add(Field(at, kept.Length, "Computed Checksum", NodeValue.Hex(computed, 2 * kept.Length), Explain.ComputedChecksum, Topics.Crc));
        data.Add(Field(at, kept.Length, "IsValid", NodeValue.Boolean(isValid), Explain.ChecksumIsValid, Topics.Crc));
    }

    // The RIFF header of the source WAVE file: the chunks up to the data chunk's header.
    private static void Riff(ReadOnlySpan<byte> kept, int length, long at, Node data)
    {
        if (kept.Length < 12 || !(kept[..4].SequenceEqual("RIFF"u8) || kept[..4].SequenceEqual("RF64"u8) || kept[..4].SequenceEqual("BW64"u8)))
        {
            return;
        }

        var riff = new Node("RIFF Chunk", at, 12) { Description = Explain.RiffChunk, Topic = Topics.Wrapper };
        riff.Add(Field(at, 4, "ID", Printable(kept[..4]), Explain.ChunkId, Topics.Wrapper));
        riff.Add(Field(at + 4, 4, "Size", NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(kept[4..]), "bytes", "byte"), Explain.ChunkSize, Topics.Wrapper));
        riff.Add(Field(at + 8, 4, "Form Type", Printable(kept[8..12]), Explain.FormType, Topics.Wrapper));
        data.Add(riff);
        Chunks(kept, length, at, data, 12);
    }

    private static void Chunks(ReadOnlySpan<byte> kept, int length, long at, Node data, int start)
    {
        int position = start;
        int listed = 0;
        while (position + 8 <= kept.Length && listed < 32)
        {
            string id = Printable(kept[position..(position + 4)]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(kept[(position + 4)..]);
            bool isData = id == "data";
            bool isFormat = id == "fmt ";
            long bodyLength = isData ? 0 : Math.Min((long)size + (size & 1), length - position - 8L);
            string name = $"{id.TrimEnd()} Chunk";
            var chunk = new Node(name, at + position, 8 + bodyLength) { Description = isFormat ? Explain.FormatChunk : isData ? Explain.DataChunk : Explain.RiffOtherChunk, Topic = Topics.Wrapper };
            chunk.Add(Field(at + position, 4, "ID", id, Explain.ChunkId, Topics.Wrapper));
            chunk.Add(Field(at + position + 4, 4, "Size", NodeValue.Integer(size, "bytes", "byte"), isData ? Explain.DataChunkSize : Explain.ChunkSize, Topics.Wrapper));
            if (isFormat && size >= 16 && bodyLength >= 16 && position + 8 + 16 <= kept.Length)
            {
                WaveFormat(kept[(position + 8)..], (int)Math.Min(size, bodyLength), at + position + 8, chunk);
            }

            data.Add(chunk);
            listed++;
            if (isData)
            {
                return;
            }

            long next = position + 8 + (long)size + (size & 1);
            if (next > length)
            {
                return;
            }

            position = (int)next;
        }
    }

    private static void WaveFormat(ReadOnlySpan<byte> f, int size, long at, Node chunk)
    {
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
        string tagName = tag switch { 1 => "PCM", 3 => "IEEE float", 0xFFFE => "extensible", _ => $"0x{tag:X4}" };
        chunk.Add(Field(at, 2, "Format Tag", tagName, Explain.WaveFormatTag, Topics.Wrapper));
        chunk.Add(Field(at + 2, 2, "Channels", NodeValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(f[2..]), "channels", "channel"), Explain.WaveChannels, Topics.Wrapper));
        chunk.Add(Field(at + 4, 4, "Sample Rate", NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(f[4..]), "Hz"), Explain.WaveSampleRate, Topics.Wrapper));
        chunk.Add(Field(at + 8, 4, "Byte Rate", NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(f[8..]), "bytes/s"), Explain.WaveByteRate, Topics.Wrapper));
        chunk.Add(Field(at + 12, 2, "Block Align", NodeValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(f[12..]), "bytes", "byte"), Explain.WaveBlockAlign, Topics.Wrapper));
        chunk.Add(Field(at + 14, 2, "Bits Per Sample", NodeValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(f[14..]), "bits", "bit"), Explain.WaveBits, Topics.Wrapper));
        if (tag == 0xFFFE && f.Length >= 40 && size >= 40)
        {
            chunk.Add(Field(at + 18, 2, "Valid Bits Per Sample", NodeValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(f[18..]), "bits", "bit"), Explain.WaveValidBits, Topics.Wrapper));
            chunk.Add(Field(at + 20, 4, "Channel Mask", NodeValue.Hex(BinaryPrimitives.ReadUInt32LittleEndian(f[20..]), 8), Explain.WaveChannelMask, Topics.Channels));
            ushort subFormat = BinaryPrimitives.ReadUInt16LittleEndian(f[24..]);
            chunk.Add(Field(at + 24, 16, "Sub-format", subFormat switch { 1 => "PCM", 3 => "IEEE float", _ => $"0x{subFormat:X4}" }, Explain.WaveSubFormat, Topics.Wrapper));
        }
    }

    // The header or trailer of a source file that is not a WAVE file: its first bytes say what.
    private static void AlternateWrapper(ReadOnlySpan<byte> kept, int length, long at, Node data)
    {
        if (kept.Length >= 4)
        {
            data.Add(Field(at, 4, "Signature", Printable(kept[..4]), Explain.AlternateSignature, Topics.Wrapper));
        }

        if (length > 4)
        {
            data.Add(Field(at + 4, length - 4, "Further Bytes", NodeValue.Integer(length - 4, "bytes", "byte"), Explain.AlternateFurther, Topics.Wrapper));
        }
    }
}

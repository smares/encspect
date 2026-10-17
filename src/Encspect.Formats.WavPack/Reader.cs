using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.WavPack;

// Reads the blocks of a stream one after another: a block's header, its sub-blocks, its checksum.
// The bytes of a block are read once into a buffer that serves every block, so the memory held while
// reading is one block, whatever the file's length. What the report keeps of a block is an entry; of
// the first 4,096 also what its nodes are built from.
internal static class Reader
{
    public static void Run(State s)
    {
        Source source = s.Source;
        long end = source.End;
        long position = source.Start;
        long skipFrom = -1;
        byte[] buffer = [];
        for (int step = 0; position < end; step++)
        {
            if (step % 256 == 0)
            {
                s.Cancellation.ThrowIfCancellationRequested();
            }

            if (!source.TryHeader(position, out BlockHeader header))
            {
                skipFrom = skipFrom < 0 ? position : skipFrom;
                long found = source.FindChain(position + 1, end);
                position = found >= 0 ? found : end;
                if (found >= 0)
                {
                    Skip(s, ref skipFrom, position);
                }

                continue;
            }

            Skip(s, ref skipFrom, position);
            (long length, BlockStatus status) = Extent(source, position, header);
            if (buffer.Length < length)
            {
                buffer = new byte[length];
            }

            source.Cache.Read(position, buffer.AsSpan(0, (int)length));
            Block(s, position, header, buffer.AsSpan(0, (int)length), status);
            position += length;
        }

        Skip(s, ref skipFrom, end);
        CloseSet(s);
    }

    // How long the block is: as its size says, unless that runs past the data, or leaves it not
    // followed by a header while another block begins inside it.
    private static (long Length, BlockStatus Status) Extent(Source source, long position, in BlockHeader header)
    {
        long statedEnd = position + header.BlockSize;
        long end = source.End;
        if (statedEnd <= end)
        {
            if (statedEnd == end || source.TryHeader(statedEnd, out _))
            {
                return (header.BlockSize, BlockStatus.None);
            }
        }

        long chained = source.FindChain(position + BlockHeader.Length, Math.Min(statedEnd, end));
        if (chained >= 0)
        {
            return (chained - position, BlockStatus.Oversized);
        }

        return statedEnd > end ? (end - position, BlockStatus.Truncated) : (header.BlockSize, BlockStatus.None);
    }

    // Closes the run of bytes that hold no block, if one is pending, as an entry.
    private static void Skip(State s, ref long from, long to)
    {
        if (from < 0)
        {
            return;
        }

        long start = from;
        from = -1;
        if (to <= start)
        {
            return;
        }

        int index = s.Entries.Count;
        Span<byte> head = stackalloc byte[BlockHeader.Length];
        int read = s.Source.Cache.Read(start, head[..(int)Math.Min(head.Length, to - start)]);
        bool isCut = to == s.Source.End && to - start < BlockHeader.Length && BlockHeader.HasSignature(head[..read]) && read >= 4;
        s.Entries.Add(new Entry(EntryKind.Skipped, start, to - start, default, isCut ? BlockStatus.CutHeader : BlockStatus.None, 0, -1));
        s.SkippedBytes += to - start;
        if (isCut)
        {
            s.Findings.Add(Severity.Warning, "wavpack.block-truncated", () => Nodes.Skipped(s.Records, index), _ =>
                $"The data ends inside the header of a block at 0x{start:X}: {to - start} of its {BlockHeader.Length} bytes are there.", "header");
        }
        else
        {
            s.Findings.Add(Severity.Warning, "wavpack.data-skipped", () => Nodes.Skipped(s.Records, index), count =>
                $"{Bytes(to - start)} at 0x{start:X} {(to - start == 1 ? "holds" : "hold")} no block header{(count > 1 ? $", and {count - 1} more such stretches do" : string.Empty)}; blocks are found again after them.");
        }
    }

    private static void Block(State s, long offset, in BlockHeader header, Span<byte> block, BlockStatus status)
    {
        int entry = s.Entries.Count;
        Detail? detail = s.Details.Count < State.MaxDetailedBlocks ? new Detail() : null;

        // Past the detailed blocks, blocks that are a header and nothing else join a run, so a file of
        // millions of them costs one entry.
        bool isEmpty = detail is null && block.Length == BlockHeader.Length && !header.HasAudio;
        bool joins = isEmpty && entry > 0 && s.Entries[entry - 1] is { Kind: EntryKind.Run } previous && previous.End == offset;
        if (joins)
        {
            entry--;
        }

        s.Blocks++;
        s.BlockBytes += block.Length;
        if (header.HasAudio && s.FirstAudio < 0)
        {
            s.FirstAudio = entry;
            s.FirstAudioHeader = header;
        }

        Declare(s, entry, header);
        HeaderChecks(s, entry, offset, header, status, block.Length);
        BlockStatus found = Walk(s, entry, offset, header, block, detail, status, out int count, out int wrapper);
        status |= found;
        if (header.HasAudio)
        {
            s.AudioBlocks++;
        }
        else
        {
            s.MetadataBlocks++;
        }

        if ((status & BlockStatus.HasWv) != 0)
        {
            s.WvBlocks++;
        }

        if ((status & BlockStatus.HasWvc) != 0)
        {
            s.WvcBlocks++;
        }

        if ((status & BlockStatus.HasWvx) != 0)
        {
            s.WvxBlocks++;
        }

        if ((status & BlockStatus.HasDsd) != 0)
        {
            s.DsdBlocks++;
        }

        int detailIndex = -1;
        if (detail is not null)
        {
            detailIndex = s.Details.Count;
            s.Details.Add(detail);
        }

        if (isEmpty)
        {
            if (joins)
            {
                Entry run = s.Entries[entry];
                s.Entries.SetLast(new Entry(EntryKind.Run, run.Offset, run.Length + block.Length, default, BlockStatus.None, run.SubBlocks == int.MaxValue ? int.MaxValue : run.SubBlocks + 1, -1));
            }
            else
            {
                s.Entries.Add(new Entry(EntryKind.Run, offset, block.Length, default, BlockStatus.None, 1, -1));
            }
        }
        else
        {
            s.Entries.Add(new Entry(EntryKind.Block, offset, block.Length, header, status, count, detailIndex));
        }

        if (header.HasAudio)
        {
            TrackSet(s, entry, header, offset, block.Length - wrapper);
        }
    }

    private static void HeaderChecks(State s, int entry, long offset, in BlockHeader header, BlockStatus status, int length)
    {
        ushort version = header.Version;
        if (version is < BlockHeader.MinVersion or > BlockHeader.MaxVersion)
        {
            s.Findings.Add(Severity.Warning, "wavpack.version-unsupported", () => Nodes.HeaderField(s.Records, entry, "Stream Version"), count =>
                $"The block at 0x{offset:X} has stream version 0x{version:X3}; libwavpack decodes 0x{BlockHeader.MinVersion:X3} to 0x{BlockHeader.MaxVersion:X3} and takes a header of any other for no block{(count > 1 ? $" ({count} blocks)" : string.Empty)}.");
        }

        if ((header.Flags & BlockHeader.ReservedFlag) != 0)
        {
            s.Findings.Add(Severity.Info, "wavpack.reserved-flag", () => Nodes.HeaderField(s.Records, entry, "Flags"), count =>
                $"The reserved flag, bit 27, is set in {(count == 1 ? "a block" : $"{count} blocks")}; decoders ignore it.");
        }

        if (header.IsMono && header.IsFalseStereo)
        {
            s.Findings.Add(Severity.Warning, "wavpack.flags-invalid", () => Nodes.HeaderField(s.Records, entry, "Flags"), count =>
                $"The mono flag and the false stereo flag are both set in {(count == 1 ? "a block" : $"{count} blocks")}; libwavpack refuses such a block.", "mono-and-false-stereo");
        }

        // Float and DSD blocks take their bit depth from elsewhere. For the others libwavpack subtracts the
        // shift from the sample size (open_utils.c), and its encoder writes a depth of 1 to the sample size only
        // (pack_utils.c).
        if (header.HasAudio && !header.IsDsd && !header.IsFloat && header.LeftShift >= header.BytesPerSample * 8)
        {
            int shift = header.LeftShift;
            int sampleBits = header.BytesPerSample * 8;
            s.Findings.Add(Severity.Warning, "wavpack.flags-invalid", () => Nodes.HeaderField(s.Records, entry, "Flags"), count =>
                $"The left shift of the block at 0x{offset:X} is {shift} bits, not less than the {sampleBits} bits of its samples, so the bit depth libwavpack works out as the sample size less the shift, {sampleBits - shift}, is not positive{(count > 1 ? $" ({count} blocks)" : string.Empty)}.", "left-shift");
        }

        if ((status & BlockStatus.Truncated) != 0)
        {
            long stated = header.BlockSize;
            s.Findings.Add(Severity.Warning, "wavpack.block-truncated", () => Nodes.Block(s.Records, entry), _ =>
                $"The block at 0x{offset:X} states {Bytes(stated)}, but the data ends after {length} of them.", "block");
        }
        else if ((status & BlockStatus.Oversized) != 0)
        {
            long stated = header.BlockSize;
            s.Findings.Add(Severity.Warning, "wavpack.block-size-mismatch", () => Nodes.HeaderField(s.Records, entry, "Block Size"), count =>
                $"The block at 0x{offset:X} states {stated} bytes, but another block begins {length} bytes in{(count > 1 ? $" ({count} such blocks)" : string.Empty)}; the block is taken to end there.");
        }
    }

    // The sub-blocks of the block, checked as libwavpack does (WavpackVerifySingleBlock,
    // read_metadata_buff, process_metadata). Returns what the block holds.
    private static BlockStatus Walk(State s, int entry, long offset, in BlockHeader header, Span<byte> block, Detail? detail, BlockStatus status, out int count, out int wrapper)
    {
        var found = new BlockFacts();
        wrapper = 0;
        BlockStatus result = BlockStatus.None;
        int position = BlockHeader.Length;
        var stored = detail is null ? null : new List<byte>();
        count = 0;
        bool truncated = (status & BlockStatus.Truncated) != 0;
        while (block.Length - position >= 2)
        {
            int id = block[position];
            int headerLength = 2;
            int size = block[position + 1] << 1;
            if ((id & SubBlockIds.LargeBit) != 0)
            {
                if (block.Length - position < 4)
                {
                    break;
                }

                size += (block[position + 2] << 9) + (block[position + 3] << 17);
                headerLength = 4;
            }

            int function = id & 0x3F;
            bool isOdd = (id & SubBlockIds.OddSizeBit) != 0;
            int index = count;
            bool isClipped = block.Length - position - headerLength < size;
            int available = isClipped ? block.Length - position - headerLength : size;
            int dataLength = isClipped ? available : size - (isOdd ? 1 : 0);
            long atOffset = offset + position;
            if (isOdd && size == 0)
            {
                s.Findings.Add(Severity.Warning, "wavpack.subblock-size", () => Nodes.SubBlock(s.Records, entry, index), _ =>
                    $"A sub-block at 0x{atOffset:X} is flagged as odd in size and has a size of 0, which libwavpack refuses.", "odd-empty");
            }

            ReadOnlySpan<byte> data = block.Slice(position + headerLength, Math.Max(0, dataLength));
            if (detail is not null && detail.SubBlocks.Count < State.MaxListedSubBlocks && s.ListedSubBlocks < State.MaxListedSubBlocksInAll)
            {
                int keep = SubBlockIds.IsBitstream(function) ? Math.Min(data.Length, function is SubBlockIds.WvxBitstream or SubBlockIds.DsdBlock ? 8 : 0)
                    : Math.Min(data.Length, State.MaxStoredPerSubBlock);
                keep = (int)Math.Min(keep, Math.Min(State.MaxStoredPerBlock - stored!.Count, State.MaxStoredInAll - s.StoredBytes));
                if (function == SubBlockIds.DecorrTerms && detail.TermsIndex < 0)
                {
                    detail.TermsIndex = detail.SubBlocks.Count;
                }

                detail.SubBlocks.Add(new SubBlock(id, offset + position, headerLength, size, dataLength, stored.Count, keep, isClipped));
                stored.AddRange(data[..keep].ToArray());
                s.ListedSubBlocks++;
                s.StoredBytes += keep;
            }
            else if (detail is not null)
            {
                if (detail.Further == 0)
                {
                    detail.FurtherOffset = offset + position;
                }

                detail.Further++;
            }

            if (function == SubBlockIds.BlockChecksum && detail is not null && detail.ChecksumIndex < 0 && detail.SubBlocks.Count > 0
                && detail.SubBlocks[^1].Offset == offset + position)
            {
                detail.ChecksumIndex = detail.SubBlocks.Count - 1;
            }

            if (isClipped)
            {
                result |= BlockStatus.Malformed;
                found.Status |= function switch
                {
                    SubBlockIds.WvBitstream => BlockStatus.HasWv,
                    SubBlockIds.WvcBitstream => BlockStatus.HasWvc,
                    SubBlockIds.WvxBitstream => BlockStatus.HasWvx,
                    SubBlockIds.DsdBlock => BlockStatus.HasDsd,
                    _ => BlockStatus.None,
                };
                if (!truncated)
                {
                    long sizeField = size;
                    s.Findings.Add(Severity.Warning, "wavpack.subblock-overrun", () => Nodes.SubBlock(s.Records, entry, index), count2 =>
                        $"The sub-block at 0x{atOffset:X} states {Bytes(sizeField)}, but {available} are left in the block{(count2 > 1 ? $" ({count2} blocks)" : string.Empty)}.");
                }

                if (IsWrapper(function))
                {
                    wrapper += headerLength + available;
                }

                position += headerLength + available;
                count++;
                break;
            }

            SubBlockChecks(s, entry, offset, header, index, function, id, data, found, ref result, block, position, headerLength);
            if (IsWrapper(function))
            {
                wrapper += headerLength + size;
            }

            position += headerLength + size;
            count++;
        }

        if (detail is not null)
        {
            detail.Data = stored!.ToArray();
            if (detail.Further > 0)
            {
                detail.FurtherLength = offset + position - detail.FurtherOffset;
            }
        }

        int left = block.Length - position;
        if (left > 0 && (result & BlockStatus.Malformed) == 0)
        {
            result |= BlockStatus.Malformed;
            if (detail is not null)
            {
                detail.Trailing = left;
                detail.TrailingOffset = offset + position;
            }

            long trailingAt = offset + position;
            s.Findings.Add(Severity.Warning, "wavpack.block-trailing-byte", () => Nodes.Block(s.Records, entry), _ =>
                $"{Bytes(left)} at 0x{trailingAt:X} after the last sub-block of the block at 0x{offset:X} {(left == 1 ? "holds" : "hold")} no sub-block; libwavpack refuses a block that does not end with one.");
        }

        result |= Checks(s, entry, offset, header, block, detail, found, status);
        return result | found.Status;
    }

    private static bool IsWrapper(int function) =>
        function is SubBlockIds.RiffHeader or SubBlockIds.RiffTrailer or SubBlockIds.AltHeader or SubBlockIds.AltTrailer;

    private sealed class BlockFacts
    {
        public BlockStatus Status { get; set; }

        public int ChecksumSubBlocks { get; set; }

        public bool ChecksumFailed { get; set; }

        public bool IsFirstChecksum { get; set; } = true;

        public int ChecksumAt { get; set; } = -1;

        public bool HasTerms { get; set; }

        public (int Term, int Delta)[]? Terms { get; set; }

        public int Int32SentBits { get; set; }

        public int FloatFlags { get; set; }

        public bool HasFloatInfo { get; set; }

        public bool HasInt32Info { get; set; }

        public int? SampleRate { get; set; }

        public int SampleRateIndex { get; set; } = -1;

        public int DsdPower { get; set; } = -1;

        public int DsdAt { get; set; } = -1;

        public Located<ChannelInfoValue>? ChannelInfo { get; set; }

        public int ProfileAt { get; set; } = -1;

        public HybridTargetValue? Target { get; set; }
    }

    private static void SubBlockChecks(State s, int entry, long offset, in BlockHeader header, int index, int function, int id, ReadOnlySpan<byte> data,
        BlockFacts found, ref BlockStatus result, Span<byte> block, int position, int headerLength)
    {
        long at = offset + position;
        bool isMono = header.IsMonoData;
        int dataLength = data.Length;
        void Size(string what, string expected)
        {
            int length = dataLength;
            s.Findings.Add(Severity.Warning, "wavpack.subblock-size", () => Nodes.SubBlock(s.Records, entry, index), count =>
                $"The {what} sub-block at 0x{at:X} holds {Bytes(length)}, but libwavpack takes {expected}{(count > 1 ? $" ({count} sub-blocks)" : string.Empty)}.", what);
        }

        switch (function)
        {
            case SubBlockIds.Dummy:
                break;
            case SubBlockIds.DecorrTerms:
                found.HasTerms = true;
                found.Terms = Decode.Terms(data, isMono);
                if (found.Terms is null)
                {
                    int length = data.Length;
                    s.Findings.Add(Severity.Warning, "wavpack.decorr-terms-invalid", () => Nodes.SubBlock(s.Records, entry, index), count =>
                        $"The decorrelation terms at 0x{at:X} are {(length > Decode.MaxTerms ? $"{length} passes, more than the {Decode.MaxTerms} libwavpack takes" : "not all valid (1 to 8, 17, 18 and, for stereo, -1 to -3)")}{(count > 1 ? $" ({count} blocks)" : string.Empty)}; libwavpack refuses the block.");
                }
                else
                {
                    s.TermsBlocks++;
                    s.TermsMinimum = Math.Min(s.TermsMinimum, found.Terms.Length);
                    s.TermsMaximum = Math.Max(s.TermsMaximum, found.Terms.Length);
                }

                break;
            case SubBlockIds.DecorrWeights:
                // A block starts with no passes (unpack_init), so weights before any terms are too many.
                if ((found.HasTerms ? found.Terms : []) is { } weightTerms && data.Length / (isMono ? 1 : 2) > weightTerms.Length)
                {
                    Size("decorrelation weights", found.HasTerms ? $"a weight (two for stereo) for each of the {Counted(weightTerms.Length, "pass", "passes")} at most" : "no weights, as no decorrelation terms come before them");
                }

                break;
            case SubBlockIds.DecorrSamples:
                if ((found.HasTerms ? found.Terms : []) is { } sampleTerms && Decode.PassesCovered(sampleTerms, data.Length, isMono, header.Version == 0x402 && header.IsHybrid) < 0)
                {
                    Size("decorrelation samples", found.HasTerms ? "the samples of the first passes, up to all of them, ending exactly" : "no samples of passes, as no decorrelation terms come before them");
                }

                break;
            case SubBlockIds.EntropyVars:
                if (data.Length != (isMono ? 6 : 12))
                {
                    Size("entropy variables", isMono ? "6 bytes for a mono block" : "12 bytes for a stereo block");
                }

                break;
            case SubBlockIds.HybridProfile:
                found.ProfileAt = index;
                found.Target = Decode.HybridTarget(header, data);
                if (!IsProfileSizeValid(header, data.Length))
                {
                    Size("hybrid profile", "a slow level (with bitrate control), a bitrate accumulator and optionally a bitrate delta for each channel");
                }

                break;
            case SubBlockIds.ShapingWeights:
                if (data.Length != 2 && data.Length < (isMono ? 4 : 8))
                {
                    Size("shaping weights", isMono ? "2 bytes, or 4 or more for a mono block" : "2 bytes, or 8 or more for a stereo block");
                }

                break;
            case SubBlockIds.FloatInfo:
                found.HasFloatInfo = true;
                if (data.Length == 4)
                {
                    found.FloatFlags = data[0];
                }
                else
                {
                    Size("float info", "4 bytes");
                }

                break;
            case SubBlockIds.Int32Info:
                found.HasInt32Info = true;
                if (data.Length == 4)
                {
                    found.Int32SentBits = data[0];
                }
                else
                {
                    Size("int32 info", "4 bytes");
                }

                break;
            case SubBlockIds.WvBitstream:
                found.Status |= BlockStatus.HasWv;
                if (data.Length == 0 || data.Length % 2 != 0)
                {
                    Size("WavPack bitstream", "a bitstream of an even number of bytes, not empty");
                }

                break;
            case SubBlockIds.WvcBitstream:
                found.Status |= BlockStatus.HasWvc;
                if (data.Length == 0 || data.Length % 2 != 0)
                {
                    Size("correction bitstream", "a bitstream of an even number of bytes, not empty");
                }

                break;
            case SubBlockIds.WvxBitstream:
                found.Status |= BlockStatus.HasWvx;
                if (data.Length <= 4 || data.Length % 2 != 0)
                {
                    Size("extra bitstream", "a CRC and a bitstream of an even number of bytes, more than 4");
                }

                break;
            case SubBlockIds.ChannelInfo:
                ChannelInfoSubBlock(s, entry, index, at, data, found);
                break;
            case SubBlockIds.ChannelIdentities:
                if (data.Length == 0 || data.Contains((byte)0))
                {
                    s.Findings.Add(Severity.Warning, "wavpack.channel-info-invalid", () => Nodes.SubBlock(s.Records, entry, index), count =>
                        $"The channel identities at 0x{at:X} are empty or hold a 0, which libwavpack refuses{(count > 1 ? $" ({count} blocks)" : string.Empty)}.", "identities");
                }

                break;
            case SubBlockIds.DsdBlock:
                found.Status |= BlockStatus.HasDsd;
                found.DsdAt = found.DsdAt < 0 ? index : found.DsdAt;
                DsdChecks(s, entry, index, at, header, data, found);
                break;
            case SubBlockIds.Md5Checksum or SubBlockIds.AltMd5Checksum:
                if (data.Length != 16)
                {
                    Size("MD5", "16 bytes");
                }
                else if (s.Md5 is null && function == SubBlockIds.Md5Checksum)
                {
                    s.Md5 = new Located<string>(Decode.Hex(data), entry, index);
                }

                break;
            case SubBlockIds.SampleRate:
                if (Decode.SampleRate(data) is { } rate)
                {
                    if (rate == 0)
                    {
                        s.Findings.Add(Severity.Warning, "wavpack.sample-rate-zero", () => Nodes.SubBlock(s.Records, entry, index), count =>
                            $"The sample rate sub-block at 0x{at:X} gives 0 Hz, which libwavpack ignores in favor of the rate in the flags{(count > 1 ? $" ({count} blocks)" : string.Empty)}.");
                    }
                    else
                    {
                        found.SampleRate = rate;
                        s.SampleRateField ??= new Located<int>(rate, entry, index);
                    }
                }
                else
                {
                    Size("sample rate", "3 or 4 bytes");
                }

                break;
            case SubBlockIds.ConfigBlock:
                if (Decode.Config(data) is { } config)
                {
                    s.Config ??= new Located<ConfigValue>(config, entry, index);
                }

                break;
            case SubBlockIds.NewConfigBlock:
                s.HasNewConfig = true;
                if (data.Length > 4 && data.Length - 4 > data[3])
                {
                    Size("new config block", $"at most {Bytes(4 + data[3])}, as its layout has {Channels(data[3])} to reorder");
                }

                if (Decode.NewConfig(data, 0) is { } newConfig)
                {
                    s.NewConfig ??= new Located<NewConfigValue>(newConfig, entry, index);
                }

                break;
            case SubBlockIds.AltExtension:
                if (data.Length is > 0 and < 8)
                {
                    s.Extension ??= new Located<string>(new string([.. data.ToArray().Where(b => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z').Select(b => (char)b)]), entry, index);
                }

                break;
            case SubBlockIds.RiffHeader or SubBlockIds.RiffTrailer or SubBlockIds.AltHeader or SubBlockIds.AltTrailer:
                break;
            case SubBlockIds.BlockChecksum:
                BlockChecksum(s, entry, offset, index, id, data, found, block, position, headerLength);
                break;
            default:
                bool isOptional = (function & SubBlockIds.OptionalBit) != 0;
                int number = function;
                if (isOptional)
                {
                    s.Findings.Add(Severity.Info, "wavpack.optional-subblock-unknown", () => Nodes.SubBlock(s.Records, entry, index), count =>
                        $"The optional sub-block with function 0x{number:X2} at 0x{at:X} is not one WavPack defines; decoders skip it{(count > 1 ? $" ({count} sub-blocks)" : string.Empty)}.");
                }
                else
                {
                    s.Findings.Add(Severity.Warning, "wavpack.subblock-unknown", () => Nodes.SubBlock(s.Records, entry, index), count =>
                        $"The sub-block with function 0x{number:X2} at 0x{at:X} is {(number == SubBlockIds.EncoderInfo ? "named in wavpack.h but never read" : "not one WavPack defines")}, and it is not optional, so libwavpack refuses the block{(count > 1 ? $" ({count} sub-blocks)" : string.Empty)}.");
                }

                break;
        }
    }

    private static bool IsProfileSizeValid(in BlockHeader header, int length)
    {
        int channels = header.IsMonoData ? 1 : 2;
        int need = 2 * channels * ((header.Flags & BlockHeader.HybridBitrateFlag) != 0 ? 2 : 1);
        return length == need || length == need + (2 * channels);
    }

    // The channel info libwavpack reads is the first one that gives channels; it checks that one fully
    // and a later one for its length only (read_channel_info), so a later one that fails the rest is
    // not one it refuses.
    private static void ChannelInfoSubBlock(State s, int entry, int index, long at, ReadOnlySpan<byte> data, BlockFacts found)
    {
        int length = data.Length;
        Located<ChannelInfoValue>? first = s.ChannelInfo ?? found.ChannelInfo;
        if (first is null)
        {
            ChannelInfoValue? channelInfo = Decode.ChannelInfo(data);
            found.ChannelInfo = channelInfo is { Channels: > 0 } info ? new Located<ChannelInfoValue>(info, entry, index) : null;
            if (channelInfo is null)
            {
                s.Findings.Add(Severity.Warning, "wavpack.channel-info-invalid", () => Nodes.SubBlock(s.Records, entry, index), count =>
                    $"The channel info at 0x{at:X} ({Bytes(length)}) is one libwavpack refuses: it needs 1 to 7 bytes, no more channels than twice the streams, no more speakers in the mask than channels and, in its long form, at least as many channels as streams{(count > 1 ? $" ({count} blocks)" : string.Empty)}.", "info");
            }

            return;
        }

        ChannelInfoValue kept = first.Value.Value;
        if (Decode.ChannelInfoFields(data) is not { } later)
        {
            s.Findings.Add(Severity.Warning, "wavpack.channel-info-invalid", () => Nodes.SubBlock(s.Records, entry, index), count =>
                $"The channel info at 0x{at:X} ({Bytes(length)}) is one libwavpack refuses: it needs 1 to 7 bytes{(count > 1 ? $" ({count} blocks)" : string.Empty)}.", "length");
        }
        else if (later.Channels != kept.Channels || later.Streams != kept.Streams || later.Mask != kept.Mask)
        {
            s.Findings.Add(Severity.Info, "wavpack.channel-info-ignored", () => Nodes.SubBlock(s.Records, entry, index), count =>
                $"The channel info at 0x{at:X} gives {Channels(later.Channels)} and a mask of 0x{later.Mask:X}, but an earlier one gave {Channels(kept.Channels)} and a mask of 0x{kept.Mask:X}; libwavpack keeps the first one that gives channels and ignores the rest{(count > 1 ? $" ({count} blocks)" : string.Empty)}.");
        }
    }

    // The only rate step libwavpack takes in the high mode (unpack_dsd.c RATE_S).
    private const int HighModeRateS = 20;

    private static void DsdChecks(State s, int entry, int index, long at, in BlockHeader header, ReadOnlySpan<byte> data, BlockFacts found)
    {
        string? problem = null;
        string kind = string.Empty;
        if (data.Length < 2)
        {
            (kind, problem) = ("size", "it needs at least 2 bytes");
        }
        else
        {
            found.DsdPower = data[0];
            if (data[0] > 31)
            {
                (kind, problem) = ("power", $"its rate power is {data[0]}, past 31");
            }
            else if (!Decode.IsDsdModeValid(data[1]))
            {
                (kind, problem) = ("mode", $"its mode is {data[1]}, not 0, 1 or 3");
            }
            else if (data[1] == 0 && data.Length - 2 != (long)header.Samples * (header.IsMonoData ? 1 : 2))
            {
                long expected = (long)header.Samples * (header.IsMonoData ? 1 : 2);
                (kind, problem) = ("stored", $"its stored mode holds {Bytes(data.Length - 2)} of audio, but the block counts {header.Samples} samples of {Channels(header.IsMonoData ? 1 : 2)}, which take exactly {Bytes(expected)}");
            }
            else if (data[1] == 1 && (data.Length < 3 || data[2] > 5))
            {
                (kind, problem) = ("history", "its history bits are missing or past 5");
            }
            else if (data[1] == 1 && data.Length == 3)
            {
                (kind, problem) = ("history-end", "its data ends right after the history bits");
            }
            else if (data[1] == 3 && data.Length - 2 < (header.IsMonoData ? 13 : 20))
            {
                (kind, problem) = ("high-size", $"its high mode needs at least {(header.IsMonoData ? 13 : 20)} bytes after the mode in a {(header.IsMonoData ? "mono" : "stereo")} block, but {Bytes(data.Length - 2)} follow");
            }
            else if (data[1] == 3 && data[3] != HighModeRateS)
            {
                (kind, problem) = ("high-rate", $"its high mode has a rate step of {data[3]}, not {HighModeRateS}");
            }
        }

        if (problem is not null)
        {
            s.Findings.Add(Severity.Warning, "wavpack.dsd-invalid", () => Nodes.SubBlock(s.Records, entry, index), count =>
                $"The DSD block at 0x{at:X} is one libwavpack refuses: {problem}{(count > 1 ? $" ({count} blocks)" : string.Empty)}.", kind);
        }
    }

    private static void BlockChecksum(State s, int entry, long offset, int index, int id, ReadOnlySpan<byte> data, BlockFacts found, Span<byte> block, int position, int headerLength)
    {
        found.ChecksumSubBlocks++;
        if ((id & SubBlockIds.OddSizeBit) != 0 || data.Length is not (2 or 4))
        {
            found.ChecksumFailed = true;
            long at = offset + position;
            int length = data.Length;
            s.Findings.Add(Severity.Warning, "wavpack.subblock-size", () => Nodes.SubBlock(s.Records, entry, index), count =>
                $"The block checksum at 0x{at:X} holds {Bytes(length)}, but libwavpack takes 2 or 4 and refuses the block{(count > 1 ? $" ({count} blocks)" : string.Empty)}.", "block checksum");
            return;
        }

        uint sum = Decode.Checksum(block, position + headerLength - 2);
        if (found.IsFirstChecksum)
        {
            found.IsFirstChecksum = false;
            found.ChecksumAt = position;
        }

        if (!Decode.ChecksumMatches(sum, data))
        {
            found.ChecksumFailed = true;
        }
    }

    // Checks over the whole block once its sub-blocks are walked.
    private static BlockStatus Checks(State s, int entry, long offset, in BlockHeader header, Span<byte> block, Detail? detail, BlockFacts found, BlockStatus status)
    {
        BlockStatus result = BlockStatus.None;
        if (found.ChecksumSubBlocks > 0)
        {
            if (found.ChecksumFailed)
            {
                result |= BlockStatus.ChecksumInvalid;
                s.ChecksumInvalid++;
                s.Findings.Add(Severity.Warning, "wavpack.block-checksum-mismatch", () => ChecksumNode(s, entry), count =>
                    $"The checksum stored in the block at 0x{offset:X} is not the one worked out over its bytes{(count > 1 ? $"; {count} blocks differ" : string.Empty)}; libwavpack skips such a block.");
            }
            else
            {
                result |= BlockStatus.ChecksumValid;
                s.ChecksumValid++;
            }
        }
        else if ((header.Flags & BlockHeader.HasChecksumFlag) != 0 && (status & BlockStatus.Truncated) == 0)
        {
            result |= BlockStatus.ChecksumMissing;
            s.ChecksumMissing++;
            s.Findings.Add(Severity.Warning, "wavpack.block-checksum-missing", () => Nodes.HeaderField(s.Records, entry, "Flags"), count =>
                $"The block at 0x{offset:X} says it ends with a checksum, but holds none{(count > 1 ? $" ({count} blocks)" : string.Empty)}; libwavpack refuses it.");
        }

        if (detail is not null && found.ChecksumAt >= 0)
        {
            detail.Computed = Decode.Checksum(block, found.ChecksumAt);
        }

        uint blockSamples = header.Samples;
        bool isDsd = header.IsDsd;
        if (header.HasAudio && (found.Status & (BlockStatus.HasWv | BlockStatus.HasWvc | BlockStatus.HasDsd)) == 0)
        {
            // A block of a correction file may hold only the extra bits of floating point or large
            // integers, so whether it is a fault waits for the file.
            if ((found.Status & BlockStatus.HasWvx) != 0)
            {
                s.ExtraOnly++;
                s.ExtraOnlyFirst = s.ExtraOnlyFirst < 0 ? entry : s.ExtraOnlyFirst;
            }
            else
            {
                s.Findings.Add(Severity.Warning, "wavpack.audio-without-bitstream", () => Nodes.Block(s.Records, entry), count =>
                    $"The block at 0x{offset:X} counts {blockSamples} samples, but holds no bitstream; it cannot be decoded{(count > 1 ? $" ({count} blocks)" : string.Empty)}.");
            }
        }

        if (header.IsDsd && header.HasAudio && (found.Status & BlockStatus.HasDsd) == 0 || !header.IsDsd && (found.Status & BlockStatus.HasDsd) != 0)
        {
            s.Findings.Add(Severity.Warning, "wavpack.dsd-flag-mismatch", () => Nodes.HeaderField(s.Records, entry, "Flags"), count =>
                $"The block at 0x{offset:X} {(isDsd ? "has the DSD flag but holds no DSD block" : "holds a DSD block but not the DSD flag")}{(count > 1 ? $" ({count} blocks)" : string.Empty)}.");
        }

        if (header.HasAudio)
        {
            s.HasLossyBlocks |= (found.Status & BlockStatus.HasWvx) == 0
                && (((header.Flags & BlockHeader.Int32Flag) != 0 && found.Int32SentBits != 0) || (header.IsFloat && (found.FloatFlags & 0x2E) != 0));
            if (header.IsHybrid)
            {
                s.HybridBlocks++;
            }

            if (!header.IsMono)
            {
                s.StereoBlocks++;
                if ((header.Flags & BlockHeader.JointStereoFlag) != 0)
                {
                    s.JointStereoBlocks++;
                }
            }

            if ((header.Flags & BlockHeader.CrossDecorrFlag) != 0)
            {
                s.CrossDecorrBlocks++;
            }

            if (s.HybridTarget is null && found.Target is { } target && found.ProfileAt >= 0)
            {
                s.HybridTarget = new Located<HybridTargetValue>(target, entry, found.ProfileAt);
            }

            if (s.FirstAudio == entry)
            {
                if (found.SampleRate is { } rate)
                {
                    s.SampleRate = rate;
                }
                else if (header.SampleRate != 0)
                {
                    s.SampleRate = header.SampleRate;
                }
                else
                {
                    s.SampleRate = 44100;
                    s.SampleRateMissing = true;
                    s.Findings.Add(Severity.Info, "wavpack.sample-rate-missing", () => Nodes.HeaderField(s.Records, entry, "Flags"), _ =>
                        $"The first audio block at 0x{offset:X} gives no sample rate in its flags (index 15) and holds no sample rate sub-block with a rate above 0; libwavpack then takes 44100 Hz.");
                }

                s.DsdPower = found.DsdPower;
                if (header.IsDsd && found.DsdPower is >= 0 and <= 31 && found.DsdAt >= 0 && ((long)s.SampleRate << found.DsdPower) > MaxNativeRate / 8)
                {
                    long stored = s.SampleRate;
                    int power = found.DsdPower;
                    int dsdAt = found.DsdAt;
                    s.Findings.Add(Severity.Warning, "wavpack.sample-rate-implausible", () => Nodes.SubBlock(s.Records, entry, dsdAt), _ =>
                        $"The first audio block at 0x{offset:X} gives a DSD bit rate of {stored} Hz times 2^{power} times 8, more than the {MaxNativeRate} Hz a sample rate can hold; the report takes it as {MaxNativeRate} Hz.");
                }
            }
        }

        if (found.ChannelInfo is { } channelInfo && s.ChannelInfo is null)
        {
            s.ChannelInfo = channelInfo;
        }

        return result;
    }

    private static Node ChecksumNode(State s, int entry)
    {
        Entry e = s.Entries[entry];
        return e.Detail >= 0 && s.Details[e.Detail].ChecksumIndex >= 0 ? Nodes.SubBlock(s.Records, entry, s.Details[e.Detail].ChecksumIndex) : Nodes.Block(s.Records, entry);
    }

    // The sample count the file declares, as libwavpack finds it: in the first block of index 0 with
    // a count when it holds no audio, else in the first block with audio, where an index other than 0
    // or a count of -1 leaves it unknown (WavpackOpenFileInputEx64).
    private static void Declare(State s, int entry, in BlockHeader header)
    {
        if (s.DeclaredEntry >= 0 || s.IsDeclaredUnknown)
        {
            return;
        }

        if (header.HasAudio)
        {
            if (header.BlockIndex == 0 && header.TotalSamples is { } total)
            {
                s.DeclaredTotal = total;
                s.DeclaredEntry = entry;
            }
            else
            {
                s.IsDeclaredUnknown = true;
                s.DeclaredEntry = entry;
            }
        }
        else if (header.BlockIndex == 0 && header.TotalSamples is { } declared && declared != 0)
        {
            s.DeclaredTotal = declared;
            s.DeclaredEntry = entry;
        }
    }

    // Blocks of one set (a stereo, a mono or several of them, from an initial to a final block)
    // share their first sample and length; the sets follow one another without gap.
    // length is the bytes of the block that hold audio data: those less the wrapper sub-blocks (the
    // header and trailer of the source file) a block may carry.
    private static void TrackSet(State s, int entry, in BlockHeader header, long offset, long length)
    {
        long index = header.BlockIndex;
        if (header.IsInitial || !s.SetOpen)
        {
            CloseSet(s);
            if (!header.IsInitial)
            {
                bool followsCap = s.Sets > 0 && s.SetCapped;
                bool isFirst = s.Sets == 0;
                s.Findings.Add(Severity.Warning, "wavpack.block-set-broken", () => Nodes.HeaderField(s.Records, entry, "Flags"), count =>
                    $"The block at 0x{offset:X} {(isFirst ? "starts the audio" : followsCap ? "follows a set that libwavpack ends at its stream limit" : "follows a final block")} without the initial block flag{(count > 1 ? $" ({count} such cases of broken block sets)" : string.Empty)}.", "initial");
            }

            s.SetOpen = true;
            s.SetSawFinal = false;
            s.SetCapped = false;
            s.SetOffset = offset;
            s.SetIndex = index;
            s.SetSamples = header.Samples;
            s.SetBits = 0;
            s.SetBlocks = 0;
            s.SetChannels = 0;
            s.SetFirstEntry = entry;
            s.SetFormat = header.Flags & FormatMask;
            if (s.FirstIndex < 0)
            {
                s.FirstIndex = index;
                s.FormatFlags = s.SetFormat;
                s.Sets = 0;
            }
            else if (s.SetFormat != s.FormatFlags)
            {
                s.Findings.Add(Severity.Warning, "wavpack.format-changes", () => Nodes.HeaderField(s.Records, entry, "Flags"), count =>
                    $"The block at 0x{offset:X} differs from the first audio block in sample size, sample type, sample rate or DSD, which the first block fixes for the whole file{(count > 1 ? $" ({count} sets)" : string.Empty)}.", "format");
            }

            if (s.NextIndex >= 0 && index != s.NextIndex)
            {
                long expected = s.NextIndex;
                if (index > expected)
                {
                    s.Findings.Add(Severity.Warning, "wavpack.block-index-gap", () => Nodes.HeaderField(s.Records, entry, "Block Index"), count =>
                        $"The block at 0x{offset:X} begins at sample {index}, but the block before it ends at sample {expected}: {index - expected} samples are missing{(count > 1 ? $" ({count} such gaps)" : string.Empty)}.");
                }
                else
                {
                    s.Findings.Add(Severity.Warning, "wavpack.block-index-overlap", () => Nodes.HeaderField(s.Records, entry, "Block Index"), count =>
                        $"The block at 0x{offset:X} begins at sample {index}, but the block before it ends at sample {expected}: {expected - index} samples overlap{(count > 1 ? $" ({count} such overlaps)" : string.Empty)}.");
                }
            }
        }
        else if (index != s.SetIndex || header.Samples != s.SetSamples)
        {
            long setIndex = s.SetIndex;
            uint setSamples = s.SetSamples;
            uint blockSamples = header.Samples;
            s.Findings.Add(Severity.Warning, "wavpack.block-set-broken", () => Nodes.HeaderField(s.Records, entry, "Block Index"), count =>
                $"The block at 0x{offset:X} is in a set that begins at sample {setIndex} with {setSamples} samples, but it begins at sample {index} with {blockSamples}{(count > 1 ? $" ({count} such cases of broken block sets)" : string.Empty)}.", "member");
        }

        s.SetBits += length * 8;
        s.SetBlocks++;
        s.SetChannels += header.IsMono ? 1 : 2;
        if (header.IsFinal)
        {
            s.SetSawFinal = true;
            CloseSet(s);
        }
        else if (s.SetBlocks >= MaxStreams(s))
        {
            // libwavpack ends a set at its stream limit whether or not a final block follows.
            s.SetCapped = true;
            CloseSet(s);
        }
    }

    // How many blocks libwavpack reads into one set: the streams the long form of the channel info
    // states (up to 4,096, NEW_MAX_STREAMS), else 8 (OLD_MAX_STREAMS).
    private static int MaxStreams(State s) => s.ChannelInfo?.Value.Streams ?? Decode.OldMaxStreams;

    // What of the flags must stay the same across sets: bytes per sample, float, DSD, rate and shift.
    private const uint FormatMask = BlockHeader.BytesStoredMask | BlockHeader.FloatFlag | BlockHeader.DsdFlag | (0xFu << BlockHeader.SampleRateLsb) | (0x1Fu << BlockHeader.ShiftLsb);

    private static void CloseSet(State s)
    {
        if (!s.SetOpen)
        {
            return;
        }

        s.SetOpen = false;
        int first = s.SetFirstEntry;
        if (!s.SetSawFinal)
        {
            long offset = s.SetOffset;
            int blocks = s.SetBlocks;
            bool isCapped = s.SetCapped;
            s.Findings.Add(Severity.Warning, "wavpack.block-set-broken", () => Nodes.Block(s.Records, first), count =>
                $"The set of blocks that begins at 0x{offset:X} {(isCapped ? $"holds {blocks} blocks without a final block, as many as libwavpack reads into one set (its stream limit), and is taken to end there" : "ends without a final block")}{(count > 1 ? $" ({count} such cases of broken block sets)" : string.Empty)}.", "final");
        }

        long samples = s.SetSamples;
        if (s.Sets == 0 || s.FirstSetChannels == 0)
        {
            s.FirstSetChannels = s.SetChannels;
        }

        if (s.SetChannels != s.FirstSetChannels)
        {
            int expected = s.FirstSetChannels;
            int actual = s.SetChannels;
            long setOffset = s.SetOffset;
            s.Findings.Add(Severity.Warning, "wavpack.format-changes", () => Nodes.Block(s.Records, first), count =>
                $"The set of blocks at 0x{setOffset:X} holds {Channels(actual)}, but the first holds {Channels(expected)}{(count > 1 ? $" ({count} sets)" : string.Empty)}.", "channels");
        }

        s.Sets++;
        s.CountedSamples += samples;
        s.NextIndex = s.SetIndex + samples;
        if (s.Figures is null && s.SampleRate > 0)
        {
            bool isDsd = (s.FormatFlags & BlockHeader.DsdFlag) != 0;
            s.Figures = new StreamFigures(NativeRate(s));
            s.FiguresScale = isDsd ? 8 : 1;
        }

        s.Figures?.Add(s.SetOffset, s.SetBits, samples * s.FiguresScale, s.SetBlocks <= State.MaxSetBlocks);
    }

    // The most a rate is taken as: what the 31 bits of a sample rate hold.
    public const long MaxNativeRate = int.MaxValue;

    // The rate of the samples as the figures count them: for DSD, the bits a second, held to
    // MaxNativeRate so the product cannot overflow.
    public static long NativeRate(State s)
    {
        if ((s.FormatFlags & BlockHeader.DsdFlag) == 0 || s.DsdPower is < 0 or > 31)
        {
            return s.SampleRate;
        }

        long multiplied = (long)s.SampleRate << s.DsdPower;
        return multiplied > MaxNativeRate / 8 ? MaxNativeRate : multiplied * 8;
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bytes(long count) => count == 1 ? "1 byte" : Number(count) + " bytes";

    private static string Counted(long count, string one, string many) => count == 1 ? "1 " + one : Number(count) + " " + many;

    internal static string Channels(long count) => count == 1 ? "1 channel" : Number(count) + " channels";
}

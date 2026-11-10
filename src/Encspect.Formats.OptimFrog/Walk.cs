using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.OptimFrog;

// The walk over the blocks. A block header is four printable ASCII bytes and a length; the walk never stops on a tag's signature, only on a header
// that is not a block's, so a tag after the blocks is the host's (the rule DTS-HD's chunk walk follows).
internal static class Walk
{
    private const int ReadChunk = 1 << 16;

    // Reads the header at a position: false where fewer than 8 bytes remain or the ID is not four printable bytes.
    public static bool TryReadHeader(ByteReader reader, long position, out uint id, out uint length)
    {
        id = length = 0;
        if (reader.End - position < Layout.BlockHeaderLength)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[Layout.BlockHeaderLength];
        reader.Position = position;
        reader.Read(head);
        id = BinaryPrimitives.ReadUInt32LittleEndian(head);
        length = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        return Layout.IsPrintable(id);
    }

    // Whether the block at a position is accepted as part of the file, and where it ends (which lies past the data for a known block cut short).
    // A known ID is accepted whatever its length; an unknown one only when the block fits and either ends the data or is followed by a block
    // header whose length fits too.
    public static bool Accepts(ByteReader reader, long position, out uint id, out uint length, out long end, bool isCorrection = false)
    {
        end = position;
        if (!TryReadHeader(reader, position, out id, out length))
        {
            return false;
        }

        end = position + Layout.BlockHeaderLength + length;
        if (isCorrection ? id == Layout.Corr : Layout.IsKnown(id))
        {
            return true;
        }

        if (end > reader.End)
        {
            return false;
        }

        // A block that starts at or runs over the start of a trailing tag that validates structurally is not a block: the tag is stronger evidence.
        long tagStart = TrailingTagStart(reader);
        if (position >= tagStart || end > tagStart)
        {
            return false;
        }

        return end == tagStart || (TryReadHeader(reader, end, out _, out uint next) && end + Layout.BlockHeaderLength + next <= tagStart);
    }

    // The start of the trailing tags the host looks for (an ID3v1 tag at the end, an APEv2 footer or a Lyrics3 end marker before it) that validate
    // structurally; the end of the data where there is none.
    private static long TrailingTagStart(ByteReader reader)
    {
        long start = reader.End;
        Span<byte> bytes = stackalloc byte[32];
        if (start - reader.Start > 128 + 32)
        {
            Read(reader, start - 128, bytes[..3]);
            if (bytes[..3].SequenceEqual("TAG"u8))
            {
                start -= 128;
            }
        }

        if (start - reader.Start > 32)
        {
            Read(reader, start - 32, bytes);
            if (bytes[..8].SequenceEqual("APETAGEX"u8))
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
                bool hasHeader = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) & 0x80000000) != 0;
                long tagStart = start - size - (hasHeader ? 32 : 0);
                if (size >= 32 && tagStart >= reader.Start)
                {
                    return tagStart;
                }
            }
        }

        if (start - reader.Start > 9 + 6)
        {
            Read(reader, start - 9, bytes[..9]);
            if (bytes[..9].SequenceEqual("LYRICS200"u8))
            {
                Read(reader, start - 15, bytes[..6]);
                long size = 0;
                foreach (byte b in bytes[..6])
                {
                    if (b < (byte)'0' || b > (byte)'9')
                    {
                        return start;
                    }

                    size = size * 10 + (b - '0');
                }

                if (size >= 11 && start - 15 - size >= reader.Start)
                {
                    return start - 15 - size;
                }
            }
        }

        return start;
    }

    private static void Read(ByteReader reader, long position, Span<byte> into)
    {
        reader.Position = position;
        reader.Read(into);
    }

    // The end of the last block the walk accepts, without reading any block's data: where the host looks for trailing tags.
    public static long End(ByteReader reader, int limit = int.MaxValue, bool isCorrection = false)
    {
        long position = reader.Start;
        for (int count = 0; count < limit && Accepts(reader, position, out _, out _, out long end, isCorrection); count++)
        {
            position = end;
            if (position >= reader.End)
            {
                break;
            }
        }

        return position;
    }

    public static void Run(State s, ByteReader reader)
    {
        Records records = s.Records;
        records.Offset = reader.Start;
        records.DataEnd = reader.End;
        long position = reader.Start;
        int rank = -1;
        byte[] buffer = new byte[ReadChunk];
        s.IsCorrection = TryReadHeader(reader, position, out uint firstId, out _) && firstId == Layout.Corr;
        while (Accepts(reader, position, out uint id, out uint length, out long end, s.IsCorrection))
        {
            var block = new BlockRecord { Offset = position, Length = length, Id = id };
            long held = Math.Min(end, reader.End) - position - Layout.BlockHeaderLength;
            bool isCut = end > reader.End;
            if (isCut)
            {
                block.Status |= BlockStatus.Cut;
            }

            int index = records.Counted < Records.MaxBlocks ? records.Blocks.Count : -1;
            int ordinal = (int)Math.Min(records.Counted, int.MaxValue);
            int blockRank = s.IsCorrection ? -1 : Layout.Rank(id);
            if (blockRank >= 0)
            {
                if (blockRank < rank || (blockRank == rank && blockRank != Layout.Rank(Layout.Comp)))
                {
                    block.Status |= BlockStatus.OutOfOrder;
                }
                else
                {
                    rank = blockRank;
                }
            }
            else if (s.IsCorrection ? id != Layout.Corr : id != Layout.Deta)
            {
                block.Status |= BlockStatus.Unknown;
            }

            switch ((block.Status & BlockStatus.Unknown) != 0 ? 0u : id)
            {
                case Layout.Main or Layout.MainFloat:
                    ReadMain(s, reader, position, held, length, ref block);
                    break;
                case Layout.Head or Layout.Tail:
                    bool isHead = id == Layout.Head;
                    bool isFirst = isHead ? !s.HasHead : !s.HasTail;
                    if (isHead)
                    {
                        s.HasHead = true;
                        records.HeadIndex = records.HeadIndex < 0 ? ordinal : records.HeadIndex;
                    }
                    else
                    {
                        s.HasTail = true;
                        records.TailIndex = records.TailIndex < 0 ? ordinal : records.TailIndex;
                    }

                    if (isFirst && index >= 0 && held > 0)
                    {
                        long at = position + Layout.BlockHeaderLength;
                        s.Source.Headers[at] = HeaderData.Analyze(s, reader, at, held, index, isHead, IsRiffFile(s));
                    }

                    break;
                case Layout.Comp:
                    ReadComp(s, reader, position, held, ordinal, buffer, ref block);
                    break;
                case Layout.Corr:
                    ReadCorr(reader, position, held, buffer, ref block);
                    break;
                case Layout.Md5Id:
                    s.HasMd5 = true;
                    records.Md5Index = records.Md5Index < 0 ? ordinal : records.Md5Index;
                    if (length != Layout.Md5Length)
                    {
                        block.Status |= BlockStatus.SizeInvalid;
                    }
                    else if (!isCut)
                    {
                        reader.Position = position + Layout.BlockHeaderLength;
                        records.Md5 = reader.ReadBytes(Layout.Md5Length);
                    }

                    break;
            }

            Tally(s, in block, ordinal);
            if (index >= 0)
            {
                records.Blocks.Add(block);
            }

            records.Counted++;
            position = end;
            if (isCut || position >= reader.End)
            {
                break;
            }
        }

        records.End = Math.Min(position, reader.End);
    }

    // Whether the stored header is a RIFF, RF64 or BW64 WAVE header: only then is the stored footer the chunks after the data chunk.
    private static bool IsRiffFile(State s) =>
        s.Source.Headers.Values.FirstOrDefault(h => h.Name == "WAV Header Data")?.Container is WavHeader.RiffContainer or WavHeader.Rf64Container or WavHeader.Bw64Container;

    private static void ReadMain(State s, ByteReader reader, long position, long held, uint length, ref BlockRecord block)
    {
        Records records = s.Records;
        if (records.Main is not null || records.Counted > 0)
        {
            return;
        }

        records.MainIndex = 0;
        if (length < Layout.MainFields)
        {
            block.Status |= BlockStatus.SizeInvalid;
            return;
        }

        if (held < Layout.MainFields)
        {
            return;
        }

        int take = (int)Math.Min(held, Layout.MainFields + Layout.MinDecoderVersionLength);
        reader.Position = position + Layout.BlockHeaderLength;
        byte[] data = reader.ReadBytes(take);
        records.Main = MainBlock.Read(data, length);
        s.Figures = new StreamFigures(records.Main.Value.SampleRate);
    }

    private static void ReadComp(State s, ByteReader reader, long position, long held, int ordinal, byte[] buffer, ref BlockRecord block)
    {
        if (block.Length < Layout.CompFields)
        {
            block.Status |= BlockStatus.SizeInvalid;
            return;
        }

        if (held < Layout.CompFields)
        {
            return;
        }

        Span<byte> fields = stackalloc byte[Layout.CompFields];
        reader.Position = position + Layout.BlockHeaderLength;
        reader.Read(fields);
        block.HasFields = true;
        block.StoredCrc = BinaryPrimitives.ReadUInt32LittleEndian(fields);
        block.Samples = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
        block.SampleType = fields[8];
        block.ChannelConfiguration = fields[9];
        block.Reserved = BinaryPrimitives.ReadUInt16LittleEndian(fields[10..]);
        block.EncoderId = BinaryPrimitives.ReadUInt16LittleEndian(fields[12..]);
        if (s.Records.Main is { } main && (block.SampleType != main.SampleType || block.ChannelConfiguration != main.ChannelConfiguration || block.EncoderId != main.EncoderId))
        {
            block.Status |= BlockStatus.Differs;
        }

        if ((block.Status & BlockStatus.Cut) != 0)
        {
            return;
        }

        // The CRC-32 covers the data after its own field (OptimFROG 4.509 and 5.100 files: zlib's CRC-32 of those bytes).
        uint crc = Crc32.Initial;
        reader.Position = position + Layout.BlockHeaderLength + 4;
        for (long left = block.Length - 4; left > 0;)
        {
            int n = (int)Math.Min(left, buffer.Length);
            reader.Read(buffer.AsSpan(0, n));
            crc = Crc32.Update(crc, buffer.AsSpan(0, n));
            left -= n;
        }

        block.ComputedCrc = Crc32.Finish(crc);
        block.HasCrc = true;
    }

    // A correction block: a CRC-32 over the data after it, then data the analyzer does not read.
    private static void ReadCorr(ByteReader reader, long position, long held, byte[] buffer, ref BlockRecord block)
    {
        if (block.Length < 4)
        {
            block.Status |= BlockStatus.SizeInvalid;
            return;
        }

        if (held < 4)
        {
            return;
        }

        reader.Position = position + Layout.BlockHeaderLength;
        block.StoredCrc = reader.ReadUInt32LittleEndian();
        if ((block.Status & BlockStatus.Cut) != 0)
        {
            return;
        }

        uint crc = Crc32.Initial;
        for (long left = block.Length - 4L; left > 0;)
        {
            int n = (int)Math.Min(left, buffer.Length);
            reader.Read(buffer.AsSpan(0, n));
            crc = Crc32.Update(crc, buffer.AsSpan(0, n));
            left -= n;
        }

        block.ComputedCrc = Crc32.Finish(crc);
        block.HasCrc = true;
    }

    // Adds a block to the totals and the figures.
    private static void Tally(State s, in BlockRecord block, int ordinal)
    {
        if ((block.Status & BlockStatus.Unknown) != 0)
        {
            s.UnknownBlocks++;
            s.FirstUnknown = s.FirstUnknown < 0 ? ordinal : s.FirstUnknown;
        }

        if ((block.Status & BlockStatus.OutOfOrder) != 0)
        {
            s.OutOfOrderBlocks++;
            s.FirstOutOfOrder = s.FirstOutOfOrder < 0 ? ordinal : s.FirstOutOfOrder;
        }

        if ((block.Status & BlockStatus.SizeInvalid) != 0)
        {
            s.SizeInvalidBlocks++;
            s.FirstSizeInvalid = s.FirstSizeInvalid < 0 ? ordinal : s.FirstSizeInvalid;
        }

        if ((block.Status & BlockStatus.Cut) != 0)
        {
            s.CutBlocks++;
            s.FirstCut = s.FirstCut < 0 ? ordinal : s.FirstCut;
        }

        if (block.Id == Layout.Corr && block.HasCrc)
        {
            s.CompBlocks++;
            if (!block.IsCrcValid)
            {
                s.CrcFailures++;
                if (s.FirstCrcFailure < 0)
                {
                    s.FirstCrcFailure = ordinal;
                    s.FirstCrcFailureBlock = block;
                }
            }

            return;
        }

        if (block.Id != Layout.Comp || (block.Status & BlockStatus.Unknown) != 0)
        {
            return;
        }

        if (block.HasFields)
        {
            s.ReadValues += block.Samples;
            if ((block.Status & BlockStatus.Cut) != 0)
            {
                s.LostValues += block.Samples;
            }
        }

        if ((block.Status & BlockStatus.Differs) != 0)
        {
            s.DifferingBlocks++;
            s.FirstDiffering = s.FirstDiffering < 0 ? ordinal : s.FirstDiffering;
        }

        if (!block.HasCrc)
        {
            return;
        }

        s.CompBlocks++;
        s.CompSamples += block.Samples;
        if ((block.Reserved & 0xFF) == Layout.DualStreamReservedLow)
        {
            s.DualStreamBlocks++;
        }

        if (!block.IsCrcValid)
        {
            s.CrcFailures++;
            if (s.FirstCrcFailure < 0)
            {
                s.FirstCrcFailure = ordinal;
                s.FirstCrcFailureBlock = block;
            }

        }

        if (s.Figures is { } figures && s.Records.Counted < Records.MaxBlocks)
        {
            int channels = s.Main?.Channels ?? 1;
            figures.Add(block.Offset, (block.Length + (long)Layout.BlockHeaderLength) * 8, block.Samples / channels);
        }
    }
}

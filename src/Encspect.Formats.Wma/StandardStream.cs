using Encspect.Core;

namespace Encspect.Formats.Wma;

// Figures counted while the superframes are read.
internal sealed class StandardTotals
{
    public long Superframes { get; set; }

    public long Frames { get; set; }

    public long Blocks { get; set; }

    public long FailedFrames { get; set; }

    public long SkippedSuperframes { get; set; }

    public long ObjectBytes { get; set; }

    public long UsedBytes { get; set; }

    public long FrameBits { get; set; }

    public long MinFrameBits { get; set; } = long.MaxValue;

    public long MaxFrameBits { get; set; }

    public long ContinuationSuperframes { get; set; }

    public long StraddlingFrames { get; set; }

    public long ReservoirBytes { get; set; }

    public int MaxReservoir { get; set; }

    public long MidSideBlocks { get; set; }

    public long StereoBlocks { get; set; }

    public long NoiseBlocks { get; set; }

    public long ReusedExponentBlocks { get; set; }

    public long ExponentBlocks { get; set; }

    public long CodedChannelBlocks { get; set; }

    public long UncodedBlocks { get; set; }

    public long NonZero { get; set; }

    public long Escapes { get; set; }

    public int MinGain { get; set; } = int.MaxValue;

    public int MaxGain { get; set; }

    public long[] BlocksBySize { get; } = new long[StandardParameters.BlockSizes];

    public long TailBitsAtEnd { get; set; }

    public int MaxFramesInSuperframe { get; set; }

    // Superframes without the bit reservoir that have bytes after their frame, and those whose bytes are all 'N'.
    public long PaddedSuperframes { get; set; }

    public long NPaddedSuperframes { get; set; }

    public long FillerFrames { get; set; }

    public long FillerBits { get; set; }
}

// The records a stream's nodes are built from, and nothing else: the stream's parameters and the
// lists the walk fills. The stream that wrote them also keeps the frame decoder and the bit
// reservoir, which change with every superframe and which a node builder must never reach, since it
// runs again after a collection and on several threads. Written only while the stream is walked.
internal sealed class StandardRecords(StandardParameters parameters)
{
    public StandardParameters Parameters => parameters;

    public ChunkedList<SuperframeRecord> Superframes { get; } = new();

    public ChunkedList<FrameRecord> Frames { get; } = new();

    public ChunkedList<BlockRecord> Blocks { get; } = new();

    public ChunkedList<FramePart> Parts { get; } = new();

    public Dictionary<int, ByteRange[]> Ranges { get; } = [];
}

// Walks a stream's superframes, decodes the syntax of every frame and keeps the records the nodes are
// built from: the frames of a Windows Media Audio version 1 or 2 stream, which may start in one
// superframe and end in the next (the bit reservoir), as FFmpeg's wma_decode_superframe reads them.
internal sealed class StandardStream
{
    private readonly StandardDecoder _decoder;
    private readonly List<BlockRecord> _scratch = [];

    // The bits of the frame in progress: the superframes' bytes after the last frame that ended, with
    // the bits of the first byte that belong to that frame skipped (wmadec.c: last_superframe,
    // last_superframe_len and last_bitoffset).
    private byte[] _reservoir = [];
    private int _reservoirBytes;
    private int _reservoirSkip;
    private readonly List<FramePart> _reservoirParts = [];

    public StandardStream(StandardParameters parameters, FindingCollector findings, Func<int, Node> superframeNode, Func<int, Node> frameNode)
    {
        Parameters = parameters;
        Records = new StandardRecords(parameters);
        Findings = findings;
        SuperframeNode = superframeNode;
        FrameNode = frameNode;
        _decoder = new StandardDecoder(parameters);
    }

    public StandardParameters Parameters { get; }

    public StandardRecords Records { get; }

    public FindingCollector Findings { get; }

    public Func<int, Node> SuperframeNode { get; }

    public Func<int, Node> FrameNode { get; }

    public ChunkedList<SuperframeRecord> Superframes => Records.Superframes;

    public ChunkedList<FrameRecord> Frames => Records.Frames;

    public ChunkedList<BlockRecord> Blocks => Records.Blocks;

    public ChunkedList<FramePart> Parts => Records.Parts;

    public Dictionary<int, ByteRange[]> Ranges => Records.Ranges;

    public StandardTotals Totals { get; } = new();

    // The bits a frame's superframe header takes: the index, the frame count and the bit offset.
    public int HeaderBits => 8 + Parameters.ByteOffsetBits + 3;

    // ffmpeg's wmav1 and wmav2 encoders fill every superframe after its frame with 'N' bytes (wmaenc.c) and use
    // neither the bit reservoir nor variable block lengths.
    public WmaEncoder.Trait? FFmpegTrait(int version)
    {
        StandardTotals t = Totals;
        if (Parameters.UsesBitReservoir || Parameters.UsesVariableBlockLength || t.PaddedSuperframes == 0 || t.NPaddedSuperframes != t.PaddedSuperframes)
        {
            return null;
        }

        string name = $"FFmpeg (wmav{version} encoder)";
        return new WmaEncoder.Trait(name, "FFmpeg", t.PaddedSuperframes >= 2 ? Confidence.High : Confidence.Medium,
            $"{(t.PaddedSuperframes == 1 ? "The superframe has" : $"All {t.PaddedSuperframes} padded superframes have")} bytes after the frame that are all 'N' (0x4E), which ffmpeg's wmav{version} encoder writes (wmaenc.c), with no bit reservoir and no variable block lengths");
    }

    public void Add(MappedBytes packet)
    {
        StandardParameters p = Parameters;
        int index = Superframes.Count;
        int align = p.BlockAlign;
        var record = new SuperframeRecord
        {
            Offset = packet.FileStart,
            ObjectLength = packet.Length,
            Length = Math.Min(packet.Length, align),
            RangeIndex = -1,
            FirstFrame = Frames.Count,
            ReservoirIn = Math.Max(0, _reservoirBytes),
        };

        IReadOnlyList<ByteRange> ranges = packet.Length >= align ? packet.FileRanges(0, align) : packet.FileRanges();
        if (ranges.Count > 1)
        {
            Ranges[index] = [.. ranges];
            record.RangeIndex = index;
        }

        if (ranges.Count > 0)
        {
            record.Offset = ranges[0].Offset;
        }

        Totals.Superframes++;
        Totals.ObjectBytes += packet.Length;
        if (packet.Length < align)
        {
            record.Flags |= SuperframeRecord.Skipped;
            Totals.SkippedSuperframes++;
            Superframes.Add(record);
            Findings.Add(Severity.Warning, "wma.superframe-short", () => SuperframeNode(index), count =>
                $"{(count == 1 ? "A media object holds" : $"{count} media objects hold")} fewer bytes than the block alignment of {align}, which a decoder rejects (wmadec.c); the first is at 0x{record.Offset:X}.");
            ResetReservoir();
            return;
        }

        if (packet.Length > align)
        {
            Findings.Add(Severity.Info, "wma.superframe-long", () => SuperframeNode(index), count =>
                $"{(count == 1 ? "A media object holds" : $"{count} media objects hold")} more bytes than the block alignment of {align}; a decoder takes {align} bytes as a superframe and feeds the rest in again (FFmpeg's decode.c), which this analysis does not: it reads the first {align} only.");
        }

        Totals.UsedBytes += align;
        byte[] data = packet.Span[..align].ToArray();
        var bits = new Bits(data);
        if (!p.UsesBitReservoir)
        {
            ReadSingleFrame(bits, ref record, index);
            int padding = (bits.Position + 7) >> 3;
            if (padding < align && record.Flags == 0)
            {
                Totals.PaddedSuperframes++;
                if (!data.AsSpan(padding).ContainsAnyExcept((byte)'N'))
                {
                    Totals.NPaddedSuperframes++;
                }
            }
        }
        else
        {
            ReadWithReservoir(bits, ref record, index, data);
        }

        record.FrameCount = (ushort)Math.Min(Frames.Count - record.FirstFrame, ushort.MaxValue);
        Totals.MaxFramesInSuperframe = Math.Max(Totals.MaxFramesInSuperframe, record.FrameCount);
        Superframes.Add(record);
    }

    // Without the bit reservoir a superframe is one frame (wmadec.c: nb_frames = 1).
    private void ReadSingleFrame(Bits bits, ref SuperframeRecord record, int index)
    {
        FrameStatus status = DecodeFrame(bits, index, [new FramePart(index, 0, Parameters.BlockAlign * 8)], 0, straddles: false);
        record.FrameEnd = bits.Position;
        if (status != FrameStatus.Ok)
        {
            record.Flags |= SuperframeRecord.Failed;
        }
    }

    private void ReadWithReservoir(Bits bits, ref SuperframeRecord record, int index, byte[] data)
    {
        StandardParameters p = Parameters;
        int align = p.BlockAlign;
        record.Flags |= SuperframeRecord.HasHeader;
        record.Index = (byte)bits.Read(4);
        record.RawFrames = (byte)bits.Read(4);
        int frames = record.RawFrames - (_reservoirBytes <= 0 ? 1 : 0);
        long offset = record.Offset;
        if (frames <= 0)
        {
            // No frame ends here: the whole superframe after its first byte continues the frame in progress.
            bool isError = frames < 0 || bits.Remaining <= 8;
            if (isError)
            {
                record.Flags |= SuperframeRecord.Skipped;
                Totals.SkippedSuperframes++;
                Findings.Add(Severity.Warning, "wma.superframe-header", () => SuperframeNode(index), count =>
                    $"{(count == 1 ? "A superframe states" : $"{count} superframes state")} no frame ending in {(count == 1 ? "it" : "them")}, or fewer than none, with no room for the middle of a frame; a decoder rejects {(count == 1 ? "it" : "them")}. The first is at 0x{offset:X}.");
                record.ReservoirOut = Math.Max(0, _reservoirBytes);
                return;
            }

            // wmadec.c adds 8 * buf_size - 8 to last_superframe_len here, bits to a count of bytes, so its reservoir
            // is longer than the bytes it holds and a frame that spans such a superframe is not rebuilt right. The
            // bytes this superframe contributes are the align - 1 after its first, which is what this analysis keeps.
            record.Flags |= SuperframeRecord.Continues;
            Totals.ContinuationSuperframes++;
            AppendReservoir(data, 1, align - 1);
            _reservoirParts.Add(new FramePart(index, 8, (align - 1) * 8));
            record.ReservoirOut = _reservoirBytes;
            record.FrameEnd = 8;
            Totals.MaxReservoir = Math.Max(Totals.MaxReservoir, _reservoirBytes);
            return;
        }

        int bitOffset = (int)bits.Read(p.ByteOffsetBits + 3);
        record.BitOffset = (ushort)Math.Min(bitOffset, ushort.MaxValue);
        if (bitOffset > bits.Remaining)
        {
            record.Flags |= SuperframeRecord.Failed;
            Findings.Add(Severity.Warning, "wma.bit-offset", () => SuperframeNode(index), count =>
                $"{(count == 1 ? "A superframe gives" : $"{count} superframes give")} a last frame bit offset past {(count == 1 ? "its" : "their")} end; a decoder rejects {(count == 1 ? "it" : "them")}. The first is at 0x{offset:X}.");
            ResetReservoir();
            return;
        }

        if (_reservoirBytes > 0)
        {
            // The frame that began in an earlier superframe ends here: the reservoir's bytes, then the
            // bit offset's bits, which wmadec.c copies into a buffer after them.
            record.Flags |= SuperframeRecord.Straddles;
            Totals.StraddlingFrames++;
            byte[] buffer = new byte[_reservoirBytes + ((bitOffset + 7) / 8)];
            Array.Copy(_reservoir, buffer, _reservoirBytes);
            for (int i = 0; i < bitOffset; i += 8)
            {
                int take = Math.Min(8, bitOffset - i);
                buffer[_reservoirBytes + (i / 8)] = (byte)(bits.Peek(take) << (8 - take));
                bits.Skip(take);
            }

            var joined = new Bits(buffer, (_reservoirBytes * 8) + bitOffset) { Position = _reservoirSkip };
            var parts = new List<FramePart>(_reservoirParts) { new(index, HeaderBits, bitOffset) };
            int expected = (_reservoirBytes * 8) + bitOffset;
            FrameStatus status = DecodeFrame(joined, index, parts, _reservoirSkip, straddles: true);
            if (status == FrameStatus.Ok && joined.Position != expected)
            {
                int end = joined.Position;
                if (end < expected)
                {
                    // Bits after the frame, before the next one starts. A decoder skips them (wmadec.c
                    // decodes the frame and goes on at the bit offset): the Windows Media Audio encoders
                    // fill the superframe so, with zero bits or with more coded data.
                    bool zero = joined.IsZero(end, expected);
                    Totals.FillerFrames++;
                    Totals.FillerBits += expected - end;
                    Findings.Add(Severity.Info, zero ? "wma.frame-stuffing" : "wma.frame-filler", () => FrameNode(Frames.Count - 1), count =>
                        $"{(count == 1 ? "A frame that began in the superframe before is" : $"{count} frames that began in the superframe before are")} followed by {(zero ? "zero bits" : "bits that are not zero")} up to the bit offset the superframe states, which a decoder skips; the first has {expected - end} (in the superframe at 0x{offset:X}).");
                }
            }

            frames--;
            if (status != FrameStatus.Ok)
            {
                record.Flags |= SuperframeRecord.Failed;
                ResetReservoir();
                record.FrameEnd = HeaderBits + bitOffset;
                return;
            }
        }

        int position = bitOffset + HeaderBits;
        if (position > align * 8)
        {
            record.Flags |= SuperframeRecord.Failed;
            Findings.Add(Severity.Warning, "wma.bit-offset", () => SuperframeNode(index), count =>
                $"{(count == 1 ? "A superframe's frames start" : $"{count} superframes' frames start")} past {(count == 1 ? "its" : "their")} end; a decoder rejects {(count == 1 ? "it" : "them")}.");
            ResetReservoir();
            return;
        }

        bits.Position = position;
        _decoder.ResetBlockLengths();
        FramePart[] whole = [new FramePart(index, 0, align * 8)];
        for (int i = 0; i < frames; i++)
        {
            FrameStatus status = DecodeFrame(bits, index, whole, bits.Position, straddles: false);
            if (status != FrameStatus.Ok)
            {
                record.Flags |= SuperframeRecord.Failed;
                record.FrameEnd = bits.Position;
                ResetReservoir();
                return;
            }
        }

        int endBit = bits.Position;
        record.FrameEnd = endBit;
        int start = endBit >> 3;
        int length = align - start;
        if (length < 0 || length > 32768)
        {
            record.Flags |= SuperframeRecord.Failed;
            Findings.Add(Severity.Warning, "wma.reservoir-length", () => SuperframeNode(index), count =>
                $"{(count == 1 ? "The last frame of a superframe ends" : $"The last frame of {count} superframes ends")} {(length < 0 ? "past its end" : "leaving more than 32,768 bytes")}; a decoder rejects {(count == 1 ? "it" : "them")}.");
            ResetReservoir();
            return;
        }

        _reservoir = new byte[length];
        Array.Copy(data, start, _reservoir, 0, length);
        _reservoirBytes = length;
        _reservoirSkip = endBit & 7;
        _reservoirParts.Clear();
        _reservoirParts.Add(new FramePart(index, start * 8, length * 8));
        record.ReservoirOut = length;
        Totals.ReservoirBytes += length;
        Totals.MaxReservoir = Math.Max(Totals.MaxReservoir, length);
    }

    private void AppendReservoir(byte[] data, int from, int count)
    {
        if (_reservoirBytes + count > 32768 * 2)
        {
            ResetReservoir();
            return;
        }

        if (_reservoir.Length < _reservoirBytes + count)
        {
            Array.Resize(ref _reservoir, _reservoirBytes + count);
        }

        Array.Copy(data, from, _reservoir, Math.Max(0, _reservoirBytes), count);
        _reservoirBytes = Math.Max(0, _reservoirBytes) + count;
    }

    private void ResetReservoir()
    {
        _reservoirBytes = 0;
        _reservoirSkip = 0;
        _reservoirParts.Clear();
    }

    // Reads the frame at the bits' position and keeps its records.
    private FrameStatus DecodeFrame(Bits bits, int superframe, IReadOnlyList<FramePart> parts, int startBit, bool straddles)
    {
        _scratch.Clear();
        FrameStatus status = _decoder.DecodeFrame(bits, _scratch, out int blockCount);
        int firstBlock = Blocks.Count;
        foreach (BlockRecord block in _scratch)
        {
            Blocks.Add(block);
            Count(block);
        }

        int firstPart = Parts.Count;
        foreach (FramePart part in parts)
        {
            Parts.Add(part);
        }

        Frames.Add(new FrameRecord
        {
            Superframe = superframe,
            FirstPart = firstPart,
            PartCount = (byte)parts.Count,
            FirstBlock = firstBlock,
            StartBit = startBit,
            EndBit = bits.Position,
            BlockCount = (ushort)Math.Min(blockCount, ushort.MaxValue),
            Status = (byte)status,
        });

        Totals.Frames++;
        long frameBits = bits.Position - startBit;
        if (status == FrameStatus.Ok)
        {
            Totals.FrameBits += frameBits;
            Totals.MinFrameBits = Math.Min(Totals.MinFrameBits, frameBits);
            Totals.MaxFrameBits = Math.Max(Totals.MaxFrameBits, frameBits);
        }
        else
        {
            Totals.FailedFrames++;
            int frameIndex = Frames.Count - 1;
            FrameStatus reported = status;
            Findings.Add(Severity.Warning, "wma.frame-invalid", () => FrameNode(frameIndex), count =>
                $"{(count == 1 ? "A frame is" : $"{count} frames are")} invalid: {StatusText(reported)}; a decoder drops the superframe and its bit reservoir. The first is at 0x{FrameOffset(frameIndex):X}.");
        }

        _ = straddles;
        return status;
    }

    private long FrameOffset(int frame)
    {
        FrameRecord record = Frames[frame];
        SuperframeRecord superframe = Superframes.Count > record.Superframe ? Superframes[record.Superframe] : default;
        return superframe.Offset + (record.StartBit / 8);
    }

    private void Count(in BlockRecord block)
    {
        StandardParameters p = Parameters;
        Totals.Blocks++;
        int size = p.FrameLengthBits - block.BlockBits;
        if (size >= 0 && size < Totals.BlocksBySize.Length)
        {
            Totals.BlocksBySize[size]++;
        }

        if (p.Channels == 2)
        {
            Totals.StereoBlocks++;
            if (block.IsMidSide)
            {
                Totals.MidSideBlocks++;
            }
        }

        if (block.CodedChannels == 0)
        {
            Totals.UncodedBlocks++;
            return;
        }

        Totals.CodedChannelBlocks += block.CodedChannels;
        if (block.NoiseBands0 + block.NoiseBands1 > 0)
        {
            Totals.NoiseBlocks++;
        }

        if ((block.Flags & BlockRecord.ExponentsSent) != 0)
        {
            Totals.ExponentBlocks++;
        }
        else
        {
            Totals.ReusedExponentBlocks++;
        }

        Totals.NonZero += block.NonZero0 + block.NonZero1;
        Totals.Escapes += block.Escapes0 + block.Escapes1;
        Totals.MinGain = Math.Min(Totals.MinGain, block.TotalGain);
        Totals.MaxGain = Math.Max(Totals.MaxGain, block.TotalGain);
    }

    // Called when the last superframe is in: the frame in progress never ends.
    public void Finish()
    {
        Totals.TailBitsAtEnd = _reservoirBytes > 0 ? (_reservoirBytes * 8L) - _reservoirSkip : 0;
    }

    internal static string StatusText(FrameStatus status) => status switch
    {
        FrameStatus.BlockSizeRange => "a block size index outside the stream's block sizes",
        FrameStatus.FrameOverflow => "its blocks add up to more samples than a frame holds",
        FrameStatus.GainOverread => "the total gain runs past the data",
        FrameStatus.ExponentRange => "an exponent is outside the range of the table of powers",
        FrameStatus.ExponentsMissing => "a block codes a channel but reuses exponents it never received",
        FrameStatus.CoefficientCode => "bits that are no code of the coefficient codebook",
        FrameStatus.CoefficientOverflow => "coefficients run past the end of the block",
        FrameStatus.Overrun => "the frame needs more bits than it has (FFmpeg accepts a frame read from a superframe's bit offset that ends up to 7 bits past the superframe, and does not check the other frames; this analysis reports any)",
        _ => "an error",
    };
}

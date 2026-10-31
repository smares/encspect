using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The frames of one Windows Media Audio version 1 or 2 stream, read from its media objects (docs/design/wma-standard-format.md,
// "Superframes"): a record per superframe and per frame, the bytes of the superframes in a store, and the figures over everything
// read. The nodes are built from these when asked for, by reading the frames again.
internal sealed class StdStream
{
    // The reservoir a superframe leaves is at most this many bytes, and so is what superframes inside a frame add up to.
    private const int MaxReservoir = 32_768;
    private const int MaxReservoirAppended = 65_536;

    // The middle superframes of a frame are found again when the frame is read for its nodes; this many fit one frame.
    private const int MaxMiddles = 8_192;

    // Rebuilding the file ranges of a media object from more fragments than a store item holds is not done: it maps to the whole span.
    private const int MaxRanges = 4_000;

    // The bytes of superframes kept for the nodes of their frames' blocks, up to this many; the frames of later superframes get no blocks.
    // Reading never depends on the store: the reservoir's superframes are held in a window of their own.
    internal const long MaxStoredBytes = 4L << 20;

    private readonly SortedDictionary<int, byte[]> _window = [];
    private long _stored;

    public StdStream(StdParameters parameters)
    {
        Parameters = parameters;
    }

    public StdParameters Parameters { get; }

    public ChunkedList<SuperframeRecord> Superframes { get; } = new();

    public ChunkedList<FrameRecord> Frames { get; } = new();

    public ByteStore Store { get; } = new();

    public StdTotals Totals { get; } = new();

    // Media objects handed over, and those past the limit that were counted and not read.
    public long Skipped { get; private set; }

    public int MaxFramesPerSuperframe { get; private set; }

    public long SpanningFrames { get; private set; }

    public long LargestReservoir { get; private set; }

    public long ReservoirSum { get; private set; }

    public long InsideFrame { get; private set; }

    public long FillerFrames { get; private set; }

    public long FillerBits { get; private set; }

    public long ValidFrames { get; private set; }

    public long InvalidFrames { get; private set; }

    public long SmallestFrameBits { get; private set; } = long.MaxValue;

    public long LargestFrameBits { get; private set; }

    public long NotShortSuperframes { get; private set; }

    public long PaddedSuperframes { get; private set; }

    public bool IsPaddingAllN { get; private set; } = true;

    // The reservoir after the last superframe, which no later superframe finishes.
    public int FinalReservoirBytes { get; private set; }

    public int FinalReservoirStartBit { get; private set; }

    // Whether the frame's blocks can be listed: the bytes of every superframe it lies in were kept.
    public bool HasBytes(in FrameRecord frame)
    {
        if (Superframes[frame.Superframe].DataRef == uint.MaxValue)
        {
            return false;
        }

        if (!frame.IsSpanning)
        {
            return true;
        }

        for (int i = frame.StartSuperframe; i < frame.Superframe; i++)
        {
            if (Superframes[i].DataRef == uint.MaxValue && Superframes[i].State is SuperframeState.Read or SuperframeState.Middle)
            {
                return false;
            }
        }

        return true;
    }

    public long UndetailedSuperframes { get; private set; }

    public long FramesRead => ValidFrames + InvalidFrames;

    public void Read(IEnumerable<MappedBytes> packets, int limit, StdNodes nodes, FindingCollector findings, CancellationToken cancellationToken)
    {
        StdParameters p = Parameters;
        int align = p.BlockAlign;
        byte[] buffer = new byte[align + 8];
        FrameState state = FrameState.Initial;
        var reservoir = new Reservoir();

        long count = 0;
        foreach (MappedBytes packet in packets)
        {
            if (count % 1024 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (count++ >= limit)
            {
                Skipped++;
                continue;
            }

            int index = Superframes.Count;
            var record = new SuperframeRecord { Length = Math.Min(packet.Length, align), RangesRef = uint.MaxValue, DataRef = uint.MaxValue, BitOffset = -1, FirstFrame = Frames.Count, EndBit = -1 };
            SetRanges(ref record, packet, record.Length);

            if (packet.Length < align)
            {
                record.State = SuperframeState.Short;
                reservoir.Bytes = 0;
                Superframes.Add(record);
                findings.Add(Severity.Warning, "wma.superframe-short", () => nodes.Superframe(index), n => StdText.SuperframeShort(n, align, SuperframeOffset(index)));
                continue;
            }

            NotShortSuperframes++;
            if (packet.Length > align)
            {
                findings.Add(Severity.Info, "wma.superframe-long", () => nodes.Superframe(index), n => StdText.SuperframeLong(n, align));
            }

            packet.Span[..align].CopyTo(buffer);
            if (_stored + align <= MaxStoredBytes)
            {
                record.DataRef = Store.Add(buffer.AsSpan(0, align));
                _stored += align;
            }
            else
            {
                UndetailedSuperframes++;
            }

            if (!p.UsesBitReservoir)
            {
                ReadWithoutReservoir(record, index, buffer, ref state, nodes, findings);
            }
            else
            {
                _window[index] = buffer.AsSpan(0, align).ToArray();
                ReadWithReservoir(record, index, buffer, ref state, ref reservoir, nodes, findings);
            }

            if (p.UsesBitReservoir)
            {
                int keepFrom = reservoir.Bytes > 0 ? reservoir.First : int.MaxValue;
                while (_window.Count > 0)
                {
                    int lowest = _window.Keys.First();
                    if (lowest >= keepFrom)
                    {
                        break;
                    }

                    _window.Remove(lowest);
                }
            }
        }

        FinalReservoirBytes = reservoir.Bytes;
        FinalReservoirStartBit = reservoir.StartBit;
    }

    // The frame in progress: the reservoir's bytes from a start bit on, held in the superframes from First, whose bit FirstBit it starts at.
    private struct Reservoir
    {
        public int Bytes;
        public int StartBit;
        public int First;
        public int FirstBit;
        public int Middles;
    }

    private void ReadWithReservoir(SuperframeRecord record, int index, byte[] buffer, ref FrameState state, ref Reservoir reservoir, StdNodes nodes, FindingCollector findings)
    {
        StdParameters p = Parameters;
        int align = p.BlockAlign;
        record.Index = buffer[0] >> 4;
        record.Count = buffer[0] & 15;
        bool isInProgress = reservoir.Bytes > 0;
        int due = isInProgress ? record.Count : record.Count - 1;
        if (due < 0 || (due == 0 && (8 * align) - 8 <= 8))
        {
            record.State = SuperframeState.InvalidHeader;
            record.Kept = reservoir.Bytes;
            Superframes.Add(record);
            findings.Add(Severity.Warning, "wma.superframe-header", () => nodes.Superframe(index), n => StdText.SuperframeHeader(n, SuperframeOffset(index)));
            return;
        }

        if (due == 0)
        {
            record.State = SuperframeState.Middle;
            InsideFrame++;
            if (!isInProgress)
            {
                reservoir = new Reservoir { First = index, FirstBit = 8 };
            }

            if (reservoir.Bytes + align - 1 > MaxReservoirAppended || reservoir.Middles >= MaxMiddles)
            {
                reservoir.Bytes = 0;
            }
            else
            {
                reservoir.Bytes += align - 1;
                reservoir.Middles++;
            }

            record.Kept = reservoir.Bytes;
            LargestReservoir = Math.Max(LargestReservoir, reservoir.Bytes);
            Superframes.Add(record);
            return;
        }

        int bitOffset = (int)new Bits(buffer, 8 * align) { Position = 8 }.Read(p.BitOffsetBits);
        record.BitOffset = bitOffset;
        int headerBits = 8 + p.BitOffsetBits;
        if (bitOffset > (8 * align) - headerBits)
        {
            record.State = SuperframeState.BitOffsetRejected;
            reservoir.Bytes = 0;
            Superframes.Add(record);
            findings.Add(Severity.Warning, "wma.bit-offset", () => nodes.Superframe(index), n => StdText.BitOffset(n, SuperframeOffset(index)));
            return;
        }

        record.State = SuperframeState.Read;
        Superframes.Add(record);
        int framesHere = 0;
        if (isInProgress)
        {
            FrameFault spanningFault = ReadSpanning(index, reservoir.First, reservoir.FirstBit, ref state, nodes, findings);
            framesHere++;
            if (spanningFault != FrameFault.None)
            {
                FinishSuperframe(index, framesHere, -1, 0);
                reservoir.Bytes = 0;
                return;
            }
        }

        state.Reset = true;
        int end = headerBits + bitOffset;
        var reader = new Bits(buffer, 8 * align) { Position = end };
        for (int i = 0; i < record.Count - 1; i++)
        {
            var frame = new FrameRecord { Superframe = index, StartSuperframe = index, StartBit = reader.Position, State = state.Pack() };
            FrameFault fault = StdFrameReader.Read(p, reader, ref state, Totals, null, out int blocks);
            frame.Blocks = blocks;
            frame.Fault = fault;
            frame.Bits = Math.Max(0, Math.Min(reader.Position, 8 * align) - frame.StartBit);
            framesHere++;
            int frameIndex = AddFrame(frame);
            if (fault != FrameFault.None)
            {
                ReportInvalid(findings, nodes, frameIndex, index, fault, frame.StartBit / 8);
                FinishSuperframe(index, framesHere, -1, 0);
                reservoir.Bytes = 0;
                return;
            }

            end = reader.Position;
        }

        int left = align - (end / 8);
        if (left > MaxReservoir)
        {
            reservoir.Bytes = 0;
            FinishSuperframe(index, framesHere, end, 0);
            findings.Add(Severity.Warning, "wma.reservoir-length", () => nodes.Superframe(index), n => StdText.ReservoirLength(n, true));
            return;
        }

        reservoir = new Reservoir { Bytes = left, StartBit = end & 7, First = index, FirstBit = end };
        FinishSuperframe(index, framesHere, end, left);
    }

    // Reads the frame the reservoir holds the start of, which ends in this superframe.
    private FrameFault ReadSpanning(int index, int first, int firstBit, ref FrameState state, StdNodes nodes, FindingCollector findings)
    {
        var frame = new FrameRecord { Superframe = index, StartSuperframe = first, StartBit = firstBit, State = state.Pack() };
        FrameBuffer buffer = BuildBuffer(frame, i => _window[i]);
        var bits = new Bits(buffer.Data, buffer.Total) { Position = buffer.Start };
        FrameFault fault = StdFrameReader.Read(Parameters, bits, ref state, Totals, null, out int blocks);
        frame.Blocks = blocks;
        frame.Fault = fault;
        frame.Bits = Math.Max(0, Math.Min(bits.Position, buffer.Total) - buffer.Start);
        if (fault == FrameFault.None)
        {
            int leftover = buffer.Total - bits.Position;
            if (leftover > 0)
            {
                frame.Filler = bits.IsZero(bits.Position, buffer.Total) ? FrameFiller.Stuffing : FrameFiller.Filler;
                frame.FillerBits = leftover;
                FillerFrames++;
                FillerBits += leftover;
            }
        }

        SpanningFrames++;
        int frameIndex = AddFrame(frame);
        if (frame.Filler != FrameFiller.None)
        {
            ReportFiller(findings, nodes, frameIndex, index, frame.Filler == FrameFiller.Stuffing, frame.FillerBits);
        }

        if (fault != FrameFault.None)
        {
            ReportInvalid(findings, nodes, frameIndex, index, fault, 0);
        }

        return fault;
    }

    private void ReportInvalid(FindingCollector findings, StdNodes nodes, int frameIndex, int superframe, FrameFault fault, int byteInSuperframe) =>
        findings.Add(Severity.Warning, "wma.frame-invalid", () => nodes.Frame(frameIndex), n => StdText.FrameInvalid(n, fault, SuperframeOffset(superframe) + byteInSuperframe));

    private void ReportFiller(FindingCollector findings, StdNodes nodes, int frameIndex, int superframe, bool isStuffing, int bits) =>
        findings.Add(Severity.Info, isStuffing ? "wma.frame-stuffing" : "wma.frame-filler", () => nodes.Frame(frameIndex), n => StdText.Filler(n, isStuffing, bits, SuperframeOffset(superframe)));

    public long SuperframeOffset(int index) => Superframes[index].Offset;

    // Writes the superframe's figures into its record once its frames are read.
    private void FinishSuperframe(int index, int frames, int endBit, int reservoir)
    {
        SuperframeRecord record = Superframes[index];
        record.Frames = frames;
        record.EndBit = endBit;
        record.Reservoir = reservoir;
        record.Kept = reservoir;
        Superframes.SetLast(record);
        MaxFramesPerSuperframe = Math.Max(MaxFramesPerSuperframe, frames);
        LargestReservoir = Math.Max(LargestReservoir, reservoir);
        ReservoirSum += reservoir;
    }

    private int AddFrame(FrameRecord frame)
    {
        if (frame.IsValid)
        {
            ValidFrames++;
            SmallestFrameBits = Math.Min(SmallestFrameBits, frame.Bits);
            LargestFrameBits = Math.Max(LargestFrameBits, frame.Bits);
        }
        else
        {
            InvalidFrames++;
        }

        Frames.Add(frame);
        return Frames.Count - 1;
    }

    private void ReadWithoutReservoir(SuperframeRecord record, int index, byte[] buffer, ref FrameState state, StdNodes nodes, FindingCollector findings)
    {
        StdParameters p = Parameters;
        int align = p.BlockAlign;
        var frame = new FrameRecord { Superframe = index, StartSuperframe = index, StartBit = 0, State = state.Pack() };
        var bits = new Bits(buffer, 8 * align);
        FrameFault fault = StdFrameReader.Read(p, bits, ref state, Totals, null, out int blocks);
        frame.Blocks = blocks;
        frame.Fault = fault;
        frame.Bits = Math.Max(0, Math.Min(bits.Position, 8 * align));
        record.State = SuperframeState.Read;
        int frameIndex = AddFrame(frame);
        record.Frames = 1;
        MaxFramesPerSuperframe = Math.Max(MaxFramesPerSuperframe, 1);
        if (fault != FrameFault.None)
        {
            Superframes.Add(record);
            ReportInvalid(findings, nodes, frameIndex, index, fault, 0);
            return;
        }

        record.EndBit = bits.Position;
        int padStart = (bits.Position + 7) / 8;
        if (padStart < align)
        {
            PaddedSuperframes++;
            if (IsPaddingAllN && buffer.AsSpan(padStart, align - padStart).ContainsAnyExcept((byte)'N'))
            {
                IsPaddingAllN = false;
            }
        }

        Superframes.Add(record);
    }

    // Keeps the file ranges of a media object that is not one run of bytes.
    private void SetRanges(ref SuperframeRecord record, MappedBytes packet, int length)
    {
        IReadOnlyList<ByteRange> ranges = packet.FileRanges(0, length);
        if (ranges.Count == 0)
        {
            record.Offset = packet.FileStart;
            return;
        }

        record.Offset = ranges[0].Offset;
        if (ranges.Count == 1)
        {
            return;
        }

        if (ranges.Count > MaxRanges)
        {
            // A crafted file's object of thousands of fragments maps to its whole span.
            ByteRange whole = new(ranges[0].Offset, ranges[^1].End - ranges[0].Offset);
            record.RangesRef = StoreRanges([whole]);
            record.RangeCount = 1;
            return;
        }

        record.RangesRef = StoreRanges(ranges);
        record.RangeCount = ranges.Count;
    }

    private uint StoreRanges(IReadOnlyList<ByteRange> ranges)
    {
        byte[] bytes = new byte[ranges.Count * 12];
        for (int i = 0; i < ranges.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 12), ranges[i].Offset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((i * 12) + 8), (int)ranges[i].Length);
        }

        return Store.Add(bytes);
    }

    // The bytes of a superframe kept in the store, for the nodes.
    public byte[] StoredBytes(int superframe) => Store.Span(Superframes[superframe].DataRef, Parameters.BlockAlign).ToArray();

    // The bits a frame is read from: the superframe's bytes for a frame read in one superframe; for a frame spanning superframes
    // the reservoir from its first byte on, then the bits after the header of the superframe where the frame ends.
    public FrameBuffer BuildBuffer(in FrameRecord frame, Func<int, byte[]> bytesOf)
    {
        int align = Parameters.BlockAlign;
        if (!frame.IsSpanning)
        {
            byte[] single = new byte[align + 8];
            bytesOf(frame.Superframe).CopyTo(single, 0);
            return new FrameBuffer(single, 8 * align, frame.StartBit);
        }

        int first = frame.StartBit >> 3;
        int middles = 0;
        for (int i = frame.StartSuperframe + 1; i < frame.Superframe; i++)
        {
            if (Superframes[i].State == SuperframeState.Middle)
            {
                middles++;
            }
        }

        int reservoir = (align - first) + (middles * (align - 1));
        SuperframeRecord end = Superframes[frame.Superframe];
        int bitOffset = end.BitOffset;
        byte[] data = new byte[reservoir + ((bitOffset + 7) / 8) + 8];
        int at = 0;
        bytesOf(frame.StartSuperframe).AsSpan(first).CopyTo(data);
        at += align - first;
        for (int i = frame.StartSuperframe + 1; i < frame.Superframe; i++)
        {
            if (Superframes[i].State == SuperframeState.Middle)
            {
                bytesOf(i).AsSpan(1).CopyTo(data.AsSpan(at));
                at += align - 1;
            }
        }

        AppendBits(bytesOf(frame.Superframe), 8 + Parameters.BitOffsetBits, bitOffset, data, reservoir);
        return new FrameBuffer(data, (8 * reservoir) + bitOffset, frame.StartBit & 7);
    }

    // Copies count bits from a bit of the source to a byte of the target, which is zero from there on.
    private static void AppendBits(ReadOnlySpan<byte> source, int sourceBit, int count, byte[] target, int targetByte)
    {
        int shift = sourceBit & 7;
        int from = sourceBit >> 3;
        int bytes = (count + 7) / 8;
        for (int i = 0; i < bytes; i++)
        {
            int high = from + i < source.Length ? source[from + i] : 0;
            int low = from + i + 1 < source.Length ? source[from + i + 1] : 0;
            target[targetByte + i] = (byte)(((high << shift) | (low >> (8 - shift))) & 0xFF);
        }

        int extra = (bytes * 8) - count;
        if (extra > 0 && bytes > 0)
        {
            target[targetByte + bytes - 1] &= (byte)(0xFF << extra);
        }
    }
}

internal readonly record struct FrameBuffer(byte[] Data, int Total, int Start);

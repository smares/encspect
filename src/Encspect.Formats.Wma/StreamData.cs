using Encspect.Core;

namespace Encspect.Formats.Wma;

internal enum PacketKind
{
    // Read: the header is read and its frames are listed.
    Read,

    // Shorter than the block alignment: not read.
    Short,

    // Only the header is shown: a stream whose frames are told apart by reading them across packets.
    HeaderOnly,
}

internal struct PacketRecord
{
    public PacketKind Kind;
    public byte Sequence;

    // The count of bits that finish the frame before, as the node shows it (cut to the payload's length).
    public int Count;
    public bool HasHeader;
    public int FirstFrame;
    public int FrameCount;

    // The bits left after the last frame that starts in the packet: where they begin, how many, and whether they start a frame that goes on.
    public int TailStart;
    public int TailBits;
    public bool IsTailUnfinished;
    public bool IsSeekable;
    public bool IsSpliced;
}

internal struct FrameRecord
{
    public int FirstPart;
    public int PartCount;
    public uint Trace;
    public int TraceLength;
    public int Bits;
    public int Fault;
    public int Subframes;
}

// Everything a Professional stream's reading keeps for the nodes: where the packets lie, the parts of the frames, and each frame's trace.
internal sealed class StreamData
{
    public PacketTable Table { get; } = new();

    public List<PacketRecord> Packets { get; } = [];

    public ChunkedList<FrameRecord> Frames { get; } = new();

    public ChunkedList<FramePart> Parts { get; } = new();

    public ByteStore Traces { get; } = new();

    public FramePart[] PartsOf(in FrameRecord frame)
    {
        var parts = new FramePart[frame.PartCount];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = Parts[frame.FirstPart + i];
        }

        return parts;
    }
}

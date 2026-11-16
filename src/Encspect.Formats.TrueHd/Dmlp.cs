using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The payload of an MP4 dmlp box, the Dolby TrueHD sample entry's configuration (docs/design/truehd-format.md, "Files and containers"): the 32 bits of format
// information of the stream's first major sync, the 15-bit peak data rate in the top bits of 16 (the major sync's, without its variable-rate bit), and 32
// reserved bits. The container hands it over as the configuration's data.
internal readonly record struct Dmlp(uint FormatInfo, int PeakDataRate, uint Reserved)
{
    public const int Length = 10;

    // Null where data holds fewer bytes than the box's fields.
    public static Dmlp? Read(ReadOnlySpan<byte> data) =>
        data.Length >= Length ? new Dmlp(BinaryPrimitives.ReadUInt32BigEndian(data), BinaryPrimitives.ReadUInt16BigEndian(data[4..]) >> 1, BinaryPrimitives.ReadUInt32BigEndian(data[6..])) : null;

    public MajorSync Sync => MajorSync.FromTrueHdFormatInfo(FormatInfo);

    // The fields as nodes under the container's box.
    public IEnumerable<Node> AsNodes(long offset)
    {
        MajorSync sync = Sync;

        // The major sync's format information starts 4 bytes into it, so its nodes are placed from 4 bytes before the box's payload.
        yield return Nodes.FormatInfoNode(offset - 4, sync);
        yield return Node.FromBits("Peak Data Rate", offset, 32, 47, NodeValue.Integer(sync.SampleRate > 0 ? (((long)PeakDataRate * sync.SampleRate) + 8) / 16 : 0, "bits per second"),
            NodeKind.Detail, Explain.PeakDataRate, Topics.AccessUnits);
        yield return new Node("Reserved", offset + 6, 4, NodeValue.Hex(Reserved, 8)) { Description = Explain.ReservedWord, Topic = Topics.AccessUnits };
    }
}

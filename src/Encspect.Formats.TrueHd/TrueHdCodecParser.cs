using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The codec parser of the access units a container hands over, one unit to a packet (MP4's mlpa samples, Matroska's A_TRUEHD and A_MLP
// blocks), or as one payload of units back to back. The routes of the containers are a later step; this is the parser they will reach by the
// codec IDs "truehd" and "mlp". A packet may hold several units, which are followed by their length words. The units are read and handed to the
// decoder as in a raw file; the container frames them, so nothing is searched for.
public sealed class TrueHdCodecParser(string id) : ICodecParser
{
    public const string TrueHdId = "truehd";
    public const string MlpId = "mlp";

    public static TrueHdCodecParser TrueHd { get; } = new(TrueHdId);

    public static TrueHdCodecParser Mlp { get; } = new(MlpId);

    public string Id => id;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var analysis = new Analysis(context);
        bool isMlp = id == MlpId;
        Records records = analysis.Records;
        List<Node> leading = analysis.Leading;
        Node parent = context.Parent;
        Node stream = new(isMlp ? "MLP" : "Dolby TrueHD", parent.Offset, parent.Length, null, () => new BlockedChildren(leading, records.Entries.Count, i => Nodes.Entry(records, i)))
        {
            Kind = NodeKind.Stream,
            Description = isMlp ? Explain.MlpStream : Explain.TrueHdStream,
            Topic = Topics.AccessUnits,
        };
        analysis.Stream = stream;
        parent.Add(stream);
        if (context.Payload is { } payload)
        {
            var source = new Source(payload);
            long first = Locator.FindSyncUnit(source, payload.Start, Math.Min(payload.End, payload.Start + Locator.SearchLimit), out _);
            TrueHdParser.Run(analysis, source, first, context.CancellationToken);
        }
        else if (context.Packets is { } packets)
        {
            int index = 0;
            foreach (MappedBytes packet in packets)
            {
                if (index++ % 1024 == 0)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }

                AddPacket(analysis, packet);
            }
        }

        Summary.Finish(analysis, parent.Offset, parent.End, context);
    }

    private static void AddPacket(Analysis analysis, MappedBytes packet)
    {
        int position = 0;
        ReadOnlyMemory<byte> data = packet.Data;
        while (position < data.Length)
        {
            ReadOnlySpan<byte> span = data.Span[position..];
            int length = span.Length < 4 ? 0 : (((span[0] & 0xF) << 8) | span[1]) * 2;
            if (length < 4)
            {
                analysis.AddSkipped(packet.OffsetOf(position), data.Length - position);
                return;
            }

            int available = Math.Min(length, data.Length - position);
            analysis.AddUnit(data.Slice(position, available), packet.OffsetOf(position), available < length, false);
            position += available;
        }
    }
}

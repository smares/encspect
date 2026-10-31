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
        IEnumerator<MappedBytes>? enumerator = null;
        var buffered = new List<MappedBytes>();
        bool isMismatch = false;
        if (context.Payload is { } found)
        {
            long at = Locator.FindSyncUnit(new Source(found), found.Start, Math.Min(found.End, found.Start + Locator.SearchLimit), out MajorSync first);
            if (at >= 0 && first.IsTrueHd == isMlp)
            {
                isMismatch = true;
                isMlp = !isMlp;
            }
        }
        else if (context.Packets is { } all)
        {
            // The stream type the first usable major sync states names the stream; the container's ID only where none shows in the first packets.
            enumerator = all.GetEnumerator();
            while (buffered.Count < LookAhead && enumerator.MoveNext())
            {
                MappedBytes packet = enumerator.Current;
                buffered.Add(packet);
                if (packet.Length > 4 && MajorSync.Read(packet.Span[4..]) is { IsUsable: true } sync)
                {
                    isMismatch = sync.IsTrueHd == isMlp;
                    isMlp = !sync.IsTrueHd;
                    break;
                }
            }
        }

        Node stream = new(isMlp ? "MLP" : "Dolby TrueHD", parent.Offset, parent.Length, null, () => new BlockedChildren(leading, records.Entries.Count, i => Nodes.Entry(records, i)))
        {
            Kind = NodeKind.Stream,
            Description = isMlp ? Explain.MlpStream : Explain.TrueHdStream,
            Topic = Topics.AccessUnits,
        };
        analysis.Stream = stream;
        parent.Add(stream);
        if (isMismatch)
        {
            string stated = isMlp ? "MLP" : "Dolby TrueHD";
            string claimed = isMlp ? "Dolby TrueHD" : "MLP";
            analysis.Findings.AddNow(Severity.Warning, "truehd.codec-id-mismatch",
                $"The container's codec ID names {claimed}, but the first major sync states the stream type {stated}; the stream is read as {stated}.", stream);
        }

        if (context.Payload is { } payload)
        {
            var source = new Source(payload);
            long first = Locator.FindSyncUnit(source, payload.Start, Math.Min(payload.End, payload.Start + Locator.SearchLimit), out _);
            TrueHdParser.Run(analysis, source, first, context.CancellationToken);
        }
        else if (enumerator is not null)
        {
            using (enumerator)
            {
                int index = 0;
                foreach (MappedBytes packet in buffered)
                {
                    AddPacket(analysis, packet);
                    index++;
                }

                while (enumerator.MoveNext())
                {
                    if (index++ % 1024 == 0)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                    }

                    AddPacket(analysis, enumerator.Current);
                }
            }
        }

        Summary.Finish(analysis, parent.Offset, parent.End, context);
    }

    private const int LookAhead = 64;

    private static void AddPacket(Analysis analysis, MappedBytes packet)
    {
        // The units are framed by the container: a packet after lost ones does not continue the stream, so it waits for a major sync.
        if (packet.FollowsGap)
        {
            analysis.MarkGap();
        }

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

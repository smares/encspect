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
        try
        {
            var buffered = new List<MappedBytes>();
            long payloadFirst = -1;
            bool isMismatch = false;
            if (context.Payload is { } found)
            {
                payloadFirst = Locator.FindSyncUnit(new Source(found), found.Start, Math.Min(found.End, found.Start + Locator.SearchLimit), out MajorSync firstSync);
                if (payloadFirst >= 0 && firstSync.IsTrueHd == isMlp)
                {
                    isMismatch = true;
                    isMlp = !isMlp;
                }
            }
            else if (context.Packets is { } all)
            {
                // The stream type the first usable major sync states names the stream; the container's ID only where none shows in the first packets.
                enumerator = all.GetEnumerator();
                long bytes = 0;
                while (buffered.Count < LookAhead && bytes <= LookAheadBytes && enumerator.MoveNext())
                {
                    MappedBytes packet = enumerator.Current;
                    buffered.Add(packet);
                    bytes += packet.Length;
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
                TrueHdParser.Run(analysis, new Source(payload), payloadFirst, context.CancellationToken);
            }
            else if (enumerator is not null)
            {
                int index = 0;
                foreach (MappedBytes packet in buffered)
                {
                    AddPacket(analysis, packet);
                    index++;
                }

                buffered.Clear();
                buffered.TrimExcess();
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
        finally
        {
            enumerator?.Dispose();
        }

        Summary.Finish(analysis, parent.Offset, parent.End, context);
    }

    // The look-ahead for the first major sync: packets, and bytes (about the longest unit each) held until the stream is named.
    private const int LookAhead = 64;
    private const long LookAheadBytes = LookAhead * 8190L;

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

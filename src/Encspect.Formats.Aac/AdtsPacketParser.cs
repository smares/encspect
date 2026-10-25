using Encspect.Core;

namespace Encspect.Formats.Aac;

// AAC handed over by a container as ADTS frames, one per packet (ICodecParser "aac-adts"), which is how the bursts of IEC 61937 carry MPEG-2 and
// MPEG-4 AAC (Microsoft's KSDATAFORMAT_SUBTYPE_IEC61937_AAC is "MPEG-2/4 AAC in ADTS"). The stream's configuration is that of the first frame's
// header (ISO/IEC 14496-3 1.A.3.2.1: profile, sampling frequency index and channel configuration, which stay the same for the stream), and each
// frame's raw_data_block follows its header and error check, so the access units are read as the packets of "aac" are. A frame that does not start
// with a valid header of the stream, and a frame of several raw data blocks, are not read.
public sealed class AdtsPacketParser : ICodecParser
{
    public string Id => "aac-adts";

    public void Parse(CodecContext context)
    {
        if (context.Payload is { } payload)
        {
            RawAacParser.ParseInto(payload, context.Parent, context, context.EncoderClaims);
            return;
        }

        Node parent = context.Parent;
        var findings = new FindingCollector();
        using IEnumerator<MappedBytes> packets = context.Packets!.GetEnumerator();
        MappedBytes? first = null;
        AdtsHeader header = default;
        while (packets.MoveNext())
        {
            if (packets.Current.Length >= AdtsHeader.Length && AdtsHeader.Read(packets.Current.Span) is { IsValid: true } candidate && packets.Current.Length >= candidate.HeaderLength)
            {
                first = packets.Current;
                header = candidate;
                break;
            }

            findings.Add(Severity.Warning, "aac.adts-packet-invalid", parent, Invalid);
        }

        if (first is null)
        {
            findings.AddNow(Severity.Warning, "aac.no-configuration",
                "The container hands over AAC as ADTS frames, but none starts with a valid ADTS header, so the object type, sample rate and channels are unknown.", parent);
        }

        AdtsHeader stream = header;
        IEnumerable<MappedBytes> Units()
        {
            if (first is not { } head)
            {
                yield break;
            }

            if (Strip(head, stream, findings, parent) is { } unit)
            {
                yield return unit;
            }

            while (packets.MoveNext())
            {
                if (Strip(packets.Current, stream, findings, parent) is { } next)
                {
                    yield return next;
                }
            }
        }

        AacCodecParser.ReadUnits(context, first is null ? null : stream.Config, null, "ADTS", findings, Units());
    }

    private static string Invalid(int count) =>
        $"{(count == 1 ? "A packet does" : $"{count} packets do")} not start with a valid ADTS header of the stream (the fixed header fields of the first frame), and {(count == 1 ? "is" : "are")} not read.";

    // The raw_data_block of an ADTS frame, or null for a frame that is not read.
    private static MappedBytes? Strip(MappedBytes packet, AdtsHeader stream, FindingCollector findings, Node parent)
    {
        if (packet.Length < AdtsHeader.Length || AdtsHeader.Read(packet.Span) is not { IsValid: true } header || !header.IsSameStream(stream) || packet.Length < header.HeaderLength)
        {
            findings.Add(Severity.Warning, "aac.adts-packet-invalid", parent, Invalid);
            return null;
        }

        if (header.RawDataBlocks != 1)
        {
            findings.Add(Severity.Info, "aac.adts-multiple-blocks", parent, n =>
                $"{(n == 1 ? "An ADTS frame holds" : $"{n} ADTS frames hold")} several raw data blocks, which are not read when ADTS frames come as packets.");
            return null;
        }

        return packet.Slice(header.HeaderLength, Math.Min(packet.Length, header.FrameLength) - header.HeaderLength);
    }
}

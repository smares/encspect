using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Speex;

// Speex in Ogg (the Speex manual, "Ogg file format", and speexenc.c of Speex 1.2.1): the first
// packet is the Speex header, the second a Vorbis comment, which the "Vorbis Comment" tag parser
// reads, then extra_headers further headers and the audio packets, each holding frames_per_packet
// frames. Every frame's submodes are read where their bits say how long each layer is, so the
// report counts frames, submodes and in-band data without decoding the audio. The granule position
// of a page counts samples up to its last packet, less the encoder's lookahead, and the last page's
// says where the audio ends.
public sealed partial class SpeexParser : ICodecParser
{
    public const string CodecId = "speex";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "speex.payload-not-analyzed", "Speex handed over as one payload is not analyzed; only Ogg packets are.", context.Parent);
            return;
        }

        var stream = new StreamState(context);
        int index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            stream.Add(packet, index++);
        }

        stream.Finish();
    }

    private static string Count(long count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}

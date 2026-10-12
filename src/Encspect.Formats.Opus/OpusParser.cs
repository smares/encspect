using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Opus;

// Opus in Ogg (RFC 7845): the identification header "OpusHead", the comment header "OpusTags" (a
// Vorbis comment, which the "Vorbis Comment" tag parser reads, then optional extra data), then the
// audio packets, each one Opus packet per stream (RFC 6716; all but the last stream's
// self-delimited, appendix B). Every packet's TOC byte and framing are read and checked against
// section 3.4, so the report counts frames by mode, bandwidth and size without decoding the audio.
// Granule positions count samples at 48 kHz, whatever the input's rate, including the pre-skip the
// decoder drops at the start (RFC 7845, section 4).
public sealed partial class OpusParser : ICodecParser
{
    public const string CodecId = "opus";

    // Output is always at 48 kHz (RFC 7845, section 4).
    private const int Rate = 48000;

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "opus.payload-not-analyzed", "Opus handed over as one payload is not analyzed; only Ogg packets are.", context.Parent);
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

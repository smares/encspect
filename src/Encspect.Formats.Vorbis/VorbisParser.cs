using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Vorbis;

// Vorbis in Ogg (Vorbis I specification, sections 4 and A): three header packets, the
// identification header, the comment header (a Vorbis comment, which the "Vorbis Comment" tag
// parser reads, between the prefix "\x03vorbis" and a framing bit) and the setup header, then the
// audio packets. The setup header is walked far enough to know each mode's block size, so every
// audio packet's mode gives its block, and the overlap of consecutive blocks gives the samples it
// returns; the granule positions are checked against them. The audio itself is not decoded.
public sealed partial class VorbisParser : ICodecParser
{
    public const string CodecId = "vorbis";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "vorbis.payload-not-analyzed", "Vorbis handed over as one payload is not analyzed; only Ogg packets are.", context.Parent);
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

    // The channel order of section 4.3.9, which names the speakers of one to eight channels; more
    // are left to the application.
    internal static string ChannelMode(int channels) => Speakers(channels) is { } speakers ? ChannelLayouts.ModeName(speakers) ?? $"{channels} channels" : $"{channels} channels";

    internal static IReadOnlyList<Speaker>? Speakers(int channels) => ChannelLayouts.VorbisOrder(channels);
}

using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Aiff;

// The CHAN chunk: Core Audio's AudioChannelLayout, which Apple's Core Audio Format Specification gives as the Channel Layout chunk of CAF files
// (CAFChannelLayout): a layout tag, a channel bitmap, a count of channel descriptions and the descriptions (a label, flags and three 32-bit floats of
// coordinates). All integers are big-endian. No AIFF specification defines the chunk; it is the structure that Apple's tools and FFmpeg's muxer put in
// AIFF files, as QuickTime puts it in the chan atom of MP4 files, and the layout is worked out as MP4's is (CoreAudioLayouts).
internal static class ChannelChunk
{
    private const int DescriptionLength = 20;

    public static void Read(State s, Fields f, Node chunk, long length)
    {
        uint tag = f.Dword("Layout Tag", Explain.ChanLayoutTag, v => NodeValue.Text(CoreAudioLayouts.TagText(v)), Topics.ChannelLayouts);
        uint bitmap = f.Dword("Channel Bitmap", Explain.ChanBitmap, v => NodeValue.Hex(v, 8), Topics.ChannelLayouts);
        uint count = f.Dword("Channel Descriptions", Explain.ChanDescriptionCount, "descriptions", Topics.ChannelLayouts);
        long fit = f.Remaining / DescriptionLength;
        long read = Math.Min(count, fit);
        var labels = new List<uint>();
        Node? table = read > 0 ? f.Node.Add(new Node("Descriptions", f.Position, read * DescriptionLength, NodeValue.Integer(count, "descriptions", "description")) { Description = Explain.ChanDescriptions, Topic = Topics.ChannelLayouts }) : null;
        for (long i = 0; i < read; i++)
        {
            if (i % 4096 == 0)
            {
                s.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            long at = f.Position;
            uint label = f.ReadDword();
            uint flags = f.ReadDword();
            byte[] coordinates = f.Reader.ReadBytes(12);
            if (i >= Chunks.MaxListed)
            {
                continue;
            }

            labels.Add(label);
            var description = new Node($"Description {i}", at, DescriptionLength, NodeValue.Text(CoreAudioLayouts.LabelText(label))) { Description = Explain.ChanDescription, Topic = Topics.ChannelLayouts };
            description.Add("Label", at, 4, NodeValue.Text(CoreAudioLayouts.LabelText(label)), Explain.ChanLabel, Topics.ChannelLayouts);
            description.Add("Flags", at + 4, 4, NodeValue.Hex(flags, 8), Explain.ChanFlags, Topics.ChannelLayouts);
            description.Add("Coordinates", at + 8, 12, (flags & 3) == 0 ? "not used" : CoreAudioLayouts.CoordinatesText(coordinates), Explain.ChanCoordinates, Topics.ChannelLayouts);
            table!.Add(description);
        }

        if (count > fit)
        {
            Node target = table ?? chunk;
            long stated = count;
            long held = fit;
            s.Findings.Add(Severity.Warning, "aiff.chan-descriptions-exceed-chunk", target, _ =>
                $"The CHAN chunk states {Counts.Of(stated, "channel description", "channel descriptions")}, but its data holds room for {held}.");
        }

        bool isTagKnown = tag is CoreAudioLayouts.UseChannelDescriptions or CoreAudioLayouts.UseChannelBitmap || (tag >> 16) is CoreAudioLayouts.DiscreteInOrder or CoreAudioLayouts.UnknownLayout
            || CoreAudioLayouts.TryGetTag(tag, out _);
        if (!isTagKnown)
        {
            uint unknown = tag;
            s.Findings.Add(Severity.Info, "aiff.layout-tag-unknown", chunk.Children.First(c => c.Name == "Layout Tag"), _ =>
                $"The layout tag 0x{unknown:X8} is not one the analyzer lists, so the CHAN chunk gives no layout; the channels are taken as the Common Chunk's count says.");
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }

        IReadOnlyList<Speaker>? layout = CoreAudioLayouts.Of(tag, bitmap, count <= Chunks.MaxListed && read == count ? labels : null);
        string what = tag switch
        {
            CoreAudioLayouts.UseChannelDescriptions => "channel descriptions",
            CoreAudioLayouts.UseChannelBitmap => "channel bitmap",
            _ => "layout tag",
        };
        chunk.Value = layout is null ? NodeValue.Text("no layout the analyzer knows") : NodeValue.Text(ChannelLayouts.Describe(layout));
        if (s.Channel is null)
        {
            s.Channel = new ChannelLayoutChunk(chunk, CoreAudioLayouts.Channels(tag, bitmap, count), what, layout);
        }
    }
}

// The first CHAN chunk as far as the checks over the whole file need it: the number of channels it states, by which field, and the speakers.
internal sealed record ChannelLayoutChunk(Node Node, long Channels, string Field, IReadOnlyList<Speaker>? Layout);

using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// What the container states about a track's speakers: QuickTime's channel layout atom (chan),
// Core Audio's AudioChannelLayout, and ISO/IEC 14496-12's channel layout box (chnl) with the speaker
// positions and channel configurations of ISO/IEC 23091-3. Both sit in the audio sample entry. The
// codec's own statement wins; the container's completes a stream whose codec states none.
public sealed partial class Mp4Parser
{
    private const uint UseChannelDescriptions = CoreAudioLayouts.UseChannelDescriptions;
    private const uint UseChannelBitmap = CoreAudioLayouts.UseChannelBitmap;

    // ISO/IEC 23091-3's ChannelConfiguration values 1 to 14 as speaker positions (the indexes of
    // Positions), in the configuration's channel order; 8 is two independent mono channels, -1 a channel without a position.
    // The specification is not freely available: the speaker sets are checked against MediaInfo's and GPAC's tables and the order of
    // 1 to 7, 11, 12, 13 and 14 against FFmpeg's aac_channel_layout_map (docs/development.md, "Tables").
    private static readonly int[]?[] Configurations =
    [
        null,
        [2],
        [0, 1],
        [2, 0, 1],
        [2, 0, 1, 10],
        [2, 0, 1, 4, 5],
        [2, 0, 1, 4, 5, 3],
        [2, 6, 7, 0, 1, 4, 5, 3],
        [-1, -1],
        [0, 1, 10],
        [0, 1, 4, 5],
        [2, 0, 1, 4, 5, 10, 3],
        [2, 0, 1, 4, 5, 8, 9, 3],
        [2, 6, 7, 0, 1, 13, 14, 8, 9, 10, 3, 26, 19, 17, 18, 23, 24, 25, 20, 21, 22, 29, 27, 28],
        [2, 0, 1, 4, 5, 3, 17, 18],
    ];

    // ISO/IEC 23091-3's speaker positions by index. The names are checked against the comments of FFmpeg's iso_channel_position and
    // MediaInfo's abbreviations (docs/development.md, "Tables").
    private static readonly string[] Positions =
    [
        "left front", "right front", "center front", "low frequency enhancement", "left surround", "right surround", "left front center",
        "right front center", "rear surround left", "rear surround right", "rear center", "left surround direct", "right surround direct",
        "left side surround", "right side surround", "left wide front", "right wide front", "left front vertical height",
        "right front vertical height", "center front vertical height", "left surround vertical height rear", "right surround vertical height rear",
        "center vertical height rear", "left vertical height side surround", "right vertical height side surround", "top center surround",
        "low frequency enhancement 2", "left front vertical bottom", "right front vertical bottom", "center front vertical bottom",
        "left vertical height surround", "right vertical height surround", "reserved", "reserved", "reserved", "reserved",
        "low frequency enhancement 3", "left edge of screen", "right edge of screen", "half-way between center of screen and left edge of screen",
        "half-way between center of screen and right edge of screen", "left back surround", "right back surround",
    ];

    // QuickTime File Format, "Audio channel layout atom": a full box holding Core Audio's
    // AudioChannelLayout, big-endian: a layout tag, a channel bitmap that counts where the tag is
    // UseChannelBitmap, and channel descriptions that count where it is UseChannelDescriptions.
    private static void QuickTimeChannelLayout(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        uint tag = (uint)f.Field(node, "Layout Tag", 4, Explain.ChanLayoutTag, v => TagText((uint)v), topic: Topics.ChannelLayouts);
        uint bitmap = (uint)f.Field(node, "Channel Bitmap", 4, Explain.ChanBitmap, v => NodeValue.Hex((long)v, 8));
        uint count = f.U32(node, "Channel Descriptions", Explain.ChanDescriptionCount);
        var labels = new List<uint>();
        long descriptionsStart = f.Position;
        uint read = 0;
        for (uint i = 0; i < count && f.Remaining >= 20; i++, read++)
        {
            if (i % 4096 == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            long start = f.Position;
            if (i >= MaxListedEntries)
            {
                // Past the descriptions shown, only the position moves on: a layout of that many channels is not stated.
                f.Skip(20);
                continue;
            }

            var description = new Node("Channel Description", start, 20) { Description = Explain.ChanDescription };
            uint label = (uint)f.Field(description, "Label", 4, Explain.ChanLabel, v => LabelText((uint)v));
            uint flags = (uint)f.Field(description, "Flags", 4, Explain.ChanFlags, v => NodeValue.Hex((long)v, 8));
            long at = f.Position;
            byte[] coordinates = f.TakeBytes(12);
            description.Add(new Node("Coordinates", at, 12, (flags & 3) == 0 ? "not used" : CoordinatesText(coordinates)) { Description = Explain.ChanCoordinates });
            description.Value = LabelText(label);
            node.Add(description);
            labels.Add(label);
        }

        MoreEntries(node, read, descriptionsStart + (MaxListedEntries * 20L), f.Position);

        if (read < count)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.chan-descriptions-exceed-box",
                $"The chan box at 0x{node.Offset:X} declares {Counts.Of(count, "channel description", "channel descriptions")}, but holds room for {read}.", node);
        }

        IReadOnlyList<Speaker>? layout = CoreAudioLayouts.Of(tag, bitmap, read <= MaxListedEntries ? labels : null);
        Stated(state, node, layout, "chan");
    }

    // ISO/IEC 14496-12 12.2.4: whether the stream carries channels or objects, then for channels a
    // ChannelConfiguration of ISO/IEC 23091-3 with a map of the channels it leaves out, or 0 and a
    // speaker position per channel of the sample entry (a position of 126 adds its azimuth and
    // elevation). Version 1 is laid out differently and is not read.
    private static void IsoChannelLayout(State state, Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        if (version != 0)
        {
            if (f.Remaining > 0)
            {
                node.Add(new Node("Data", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.UnreadData });
            }

            state.Findings.AddNow(Severity.Info, "mp4.chnl-version-not-read", $"The chnl box at 0x{node.Offset:X} is of version {version}, which is not read; only version 0 is.", node);
            return;
        }

        int structure = (int)f.Field(node, "Stream Structure", 1, Explain.ChnlStreamStructure, v => (v & 3) switch
        {
            1 => $"{v} (channels)",
            2 => $"{v} (objects)",
            3 => $"{v} (channels and objects)",
            _ => $"{v} (neither)",
        });
        IReadOnlyList<Speaker>? layout = null;
        if ((structure & 1) != 0)
        {
            int defined = (int)f.Field(node, "Defined Layout", 1, Explain.ChnlDefinedLayout, v => DefinedText((int)v), topic: Topics.ChannelLayouts);
            if (defined == 0)
            {
                int channels = state.CurrentEntry?.Channels ?? 0;
                var positions = new List<int>();
                for (int i = 0; i < channels; i++)
                {
                    int position = (int)f.Field(node, "Speaker Position", 1, Explain.ChnlSpeakerPosition, v => PositionText((int)v));
                    positions.Add(position);
                    if (position == 126)
                    {
                        long at = f.Position;
                        short azimuth = (short)f.Take(2);
                        node.Add(new Node("Azimuth", at, 2, NodeValue.Integer(azimuth, "degrees")) { Description = Explain.ChnlAzimuth });
                        at = f.Position;
                        sbyte elevation = (sbyte)f.Take(1);
                        node.Add(new Node("Elevation", at, 1, NodeValue.Integer(elevation, "degrees")) { Description = Explain.ChnlElevation });
                    }
                }

                layout = positions.Count > 0 ? FromPositions(positions) : null;
            }
            else
            {
                ulong omitted = f.Field(node, "Omitted Channels", 8, Explain.ChnlOmittedChannels, v => NodeValue.Hex(v, 16));
                if (defined < Configurations.Length && Configurations[defined] is { } configuration)
                {
                    // The lowest bit stands for the configuration's first channel.
                    layout = FromPositions([.. configuration.Where((_, i) => i >= 64 || (omitted & (1UL << i)) == 0)]);
                }
            }
        }

        if ((structure & 2) != 0)
        {
            f.U8(node, "Object Count", Explain.ChnlObjectCount);
        }

        Stated(state, node, layout, "chnl");
    }

    // The box's value and the sample entry's statement, of which the first box read counts.
    private static void Stated(State state, Node node, IReadOnlyList<Speaker>? layout, string box)
    {
        node.Value = layout is null ? "no layout stated that the analyzer knows" : ChannelLayouts.Describe(layout);
        if (layout is not null && state.CurrentEntry is { Layout: null } entry)
        {
            entry.Layout = layout;
            entry.LayoutNode = node;
            entry.LayoutBox = box;
        }
    }

    // After the codec has reported the track's streams: a stream whose codec states no layout gets
    // the container's, where the channel counts agree; a codec that states other speakers than the
    // container keeps its own, with a finding. An AAC stream whose access units carry parametric
    // stereo states stereo, signaled or not, so a mono one is mono.
    private static void CompareLayouts(State state, SampleEntry entry, int streamsBefore, string label)
    {
        if (entry.Layout is not { } stated || entry.LayoutNode is not { } node)
        {
            return;
        }

        foreach (StreamInfo stream in state.Context.Streams.Skip(streamsBefore).ToList())
        {
            if (stream.ChannelLayout is null && (stream.Channels == 0 || stream.Channels == stated.Count))
            {
                state.Context.UpdateStream(stream, stream with { ChannelLayout = stated, Channels = stated.Count });
                continue;
            }

            IReadOnlyList<Speaker>? coded = stream.ChannelLayout;
            if (coded is not null && ChannelLayouts.HaveSameSpeakers(stated, coded))
            {
                continue;
            }

            string codedText = coded is null ? $"{Counts.Of(stream.Channels, "channel", "channels")}" : ChannelLayouts.Describe(coded);
            state.Findings.AddNow(Severity.Warning, "mp4.channel-layout-differs",
                $"The {entry.LayoutBox} box of {label} states {ChannelLayouts.Describe(stated)}, but the {stream.Codec} stream states {codedText}"
                + "; players that follow the container place the channels differently from the coding. The report gives the stream's channels.",
                node);
        }
    }

    internal static IReadOnlyList<Speaker> FromAbbreviations(string[] channels) => CoreAudioLayouts.FromAbbreviations(channels);

    internal static IReadOnlyList<Speaker> FromBitmap(uint bitmap) => CoreAudioLayouts.FromBitmap(bitmap);

    internal static IReadOnlyList<Speaker> FromLabels(List<uint> labels) => CoreAudioLayouts.FromLabels(labels);

    // ISO/IEC 23091-3 positions: the surround pair (4 and 5) is the back pair, unless the rear
    // pair (8 and 9) is also present, which makes it the side pair; positions the report names no
    // speaker for are discrete.
    internal static IReadOnlyList<Speaker> FromPositions(IReadOnlyList<int> positions)
    {
        bool hasRear = positions.Contains(8) || positions.Contains(9);
        return [.. positions.Select(p => p switch
        {
            0 => Speaker.FrontLeft,
            1 => Speaker.FrontRight,
            2 => Speaker.FrontCenter,
            3 => Speaker.LowFrequency,
            4 => hasRear ? Speaker.SideLeft : Speaker.BackLeft,
            5 => hasRear ? Speaker.SideRight : Speaker.BackRight,
            6 => Speaker.FrontLeftOfCenter,
            7 => Speaker.FrontRightOfCenter,
            8 => Speaker.BackLeft,
            9 => Speaker.BackRight,
            10 => Speaker.BackCenter,
            11 or 13 => Speaker.SideLeft,
            12 or 14 => Speaker.SideRight,
            15 => Speaker.FrontLeftWide,
            16 => Speaker.FrontRightWide,
            17 => Speaker.TopFrontLeft,
            18 => Speaker.TopFrontRight,
            19 => Speaker.TopFrontCenter,
            20 => Speaker.TopBackLeft,
            21 => Speaker.TopBackRight,
            22 => Speaker.TopBackCenter,
            23 => Speaker.TopSideLeft,
            24 => Speaker.TopSideRight,
            25 => Speaker.TopCenter,
            26 => Speaker.LowFrequency2,
            27 => Speaker.BottomFrontLeft,
            28 => Speaker.BottomFrontRight,
            29 => Speaker.BottomFrontCenter,
            _ => Speaker.Discrete,
        })];
    }

    private static NodeValue TagText(uint tag) => CoreAudioLayouts.TagText(tag);

    private static NodeValue LabelText(uint label) => CoreAudioLayouts.LabelText(label);

    private static string CoordinatesText(byte[] bytes) => CoreAudioLayouts.CoordinatesText(bytes);

    private static NodeValue DefinedText(int value) => value switch
    {
        0 => "0 (a speaker position per channel)",
        _ when value < Configurations.Length && Configurations[value] is { } configuration => $"{value} ({ChannelLayouts.Describe(FromPositions(configuration))})",
        _ => $"{value} (not known)",
    };

    private static NodeValue PositionText(int value) => value switch
    {
        < 43 => $"{value} ({Positions[value]})",
        126 => $"{value} (explicit position)",
        127 => $"{value} (unknown)",
        _ => $"{value} (reserved)",
    };
}

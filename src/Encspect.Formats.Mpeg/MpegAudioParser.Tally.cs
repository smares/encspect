using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// The statistics mp3guessenc and EncSpot print about a stream's coding, over a region's complete
// audio frames like the other statistics: channel modes, the stereo coding of joint stereo frames,
// Layer III block usage, global gain, scfsi, scalefactor scaling and main_data_begin, padding,
// ancillary data and the frame sizes of each bitrate. They are counted while the frames are read,
// so no frame is read twice and nothing is kept per frame.
public sealed partial class MpegAudioParser
{
    // By mode extension: Layer III bit 1 is mid/side stereo, bit 0 intensity stereo (ISO/IEC 11172-3
    // 2.4.2.3).
    private static readonly string[] JointStereoModes = ["Left/right stereo", "Intensity stereo", "Mid/side stereo", "Mid/side and intensity stereo"];

    // By FrameTally.BlockKind.
    private static readonly string[] BlockKinds = ["Long", "Start", "Short", "Mixed", "Stop", "Reserved"];

    private static void AddTally(Node node, RegionState region, long start, long length, long audioBytes, long ticks)
    {
        FrameTally tally = region.Tally;
        Node modes = node.Add("Channel Modes", start, length, NodeValue.Integer(tally.ChannelModes.Count(c => c > 0), "modes", "mode"));
        int frames = tally.ChannelModes.Sum();
        for (int mode = 0; mode < 4; mode++)
        {
            if (tally.ChannelModes[mode] > 0)
            {
                modes.Add(MpegHeader.ChannelModes[mode], start, length, Share(FramesText(tally.ChannelModes[mode]), tally.ChannelModes[mode], frames));
            }
        }

        AddExtensions("Joint Stereo Modes", tally.Layer3Extensions, e => JointStereoModes[e]);
        AddExtensions("Intensity Stereo Bounds", tally.Bounds, e => $"Bands {(e + 1) * 4} to 31");

        if (tally.Layer3Frames > 0)
        {
            Node blocks = node.Add("Block Usage", start, length, NodeValue.Integer(tally.GranuleChannels, "granules", "granule"));
            for (int kind = 0; kind < BlockKinds.Length; kind++)
            {
                if (tally.Blocks[kind] > 0)
                {
                    blocks.Add(BlockKinds[kind], start, length, Share(GranulesText(tally.Blocks[kind]), tally.Blocks[kind], tally.GranuleChannels));
                }
            }

            Node gain = node.Add(new Node("Global Gain", start, length));
            for (int ch = 0; ch < 2; ch++)
            {
                if (tally.MaximumGain[ch] >= 0)
                {
                    Node channel = gain.Add(new Node($"Channel {ch}", start, length));
                    channel.Add("Minimum", start, length, NodeValue.Integer(tally.MinimumGain[ch]));
                    channel.Add("Maximum", start, length, NodeValue.Integer(tally.MaximumGain[ch]));
                }
            }
        }

        // Layer III's ancillary segments follow mp3guessenc's scan (AncillaryScan); Layers I and II
        // count what mp3guessenc counts after the audio data.
        AncillaryScan? scan = region.Ancillary;
        long ancillary = (scan?.AudioBytes ?? 0) + tally.Layer12Ancillary;
        Node data = node.Add("Ancillary Data", start, length, Bytes(ancillary));
        data.Add("Share", start, length, NodeValue.Decimal(RoundHalfEven((Int128)ancillary * 1000, audioBytes) / 10.0, 1, "%"));
        data.Add("Data Rate", start, length, NodeValue.Decimal(RoundHalfEven((Int128)ancillary * 8 * Clock * 10, (Int128)ticks * 1000) / 10.0, 1, "kbps"));
        if (ancillary > 0)
        {
            long smallest = scan?.SmallestSegment ?? 0;
            smallest = smallest == 0 ? tally.SmallestLayer12 : tally.SmallestLayer12 == 0 ? smallest : Math.Min(smallest, tally.SmallestLayer12);
            data.Add("Smallest Nonzero Amount", start, length, Bytes(smallest));
            data.Add("Largest Amount", start, length, Bytes(Math.Max(scan?.LargestSegment ?? 0, tally.LargestLayer12)));
        }

        node.Add("UsesPadding", start, length, NodeValue.Boolean(tally.UsesPadding));
        if (tally.Layer3Frames > 0)
        {
            node.Add("UsesScfsi", start, length, NodeValue.Boolean(tally.UsesScfsi));
            node.Add("UsesScalefactorScaling", start, length, NodeValue.Boolean(tally.Scaled > 0));
            node.Add("Scalefactor Scaling", start, length, NodeValue.Decimal(RoundHalfEven((Int128)tally.Scaled * 1000, tally.GranuleChannels) / 10.0, 1, "%"));
            node.Add("Average Main Data Begin", start, length, NodeValue.Decimal(RoundHalfEven((Int128)tally.MainDataBegins * 10, tally.Layer3Frames) / 10.0, 1, "bytes"));
            node.Add("Maximum Main Data Begin", start, length, NodeValue.Integer(tally.MaximumMainDataBegin, "bytes"));
        }

        void AddExtensions(string name, int[] counts, Func<int, string> label)
        {
            int joint = counts.Sum();
            if (joint == 0)
            {
                return;
            }

            Node parent = node.Add(name, start, length, NodeValue.Integer(joint, "frames", "frame"));
            for (int e = 0; e < 4; e++)
            {
                if (counts[e] > 0)
                {
                    parent.Add(label(e), start, length, Share(FramesText(counts[e]), counts[e], joint));
                }
            }
        }
    }

    // The frames of one bitrate by padding: "7 frames of 1044 bytes", or each length where a region
    // holds several streams or a free-format length changes.
    private static void AddFrameSizes(Node entry, FrameTally tally, int? bitrate)
    {
        foreach (bool padded in new[] { false, true })
        {
            var sizes = tally.Sizes.Where(s => s.Key.Bitrate == bitrate && s.Key.IsPadded == padded).OrderBy(s => s.Key.Length).ToList();
            if (sizes.Count > 0)
            {
                entry.Add(padded ? "Padded" : "Unpadded", entry.Offset, entry.Length,
                    string.Join(", ", sizes.Select(s => $"{FramesText(s.Value)} of {Number(s.Key.Length)} bytes")));
            }
        }
    }

    private static string Share(string count, long part, long whole) => $"{count} ({TenthsText(RoundHalfEven(part * 1000L, whole))}%)";

    private static string GranulesText(int count) => count == 1 ? "1 granule" : $"{Number(count)} granules";

    // Counts of a region's complete audio frames.
    private sealed class FrameTally
    {
        public int[] ChannelModes { get; } = new int[4];

        // Joint stereo frames by mode extension: Layer III's stereo coding, Layers I and II's
        // intensity stereo bound.
        public int[] Layer3Extensions { get; } = new int[4];

        public int[] Bounds { get; } = new int[4];

        public bool UsesPadding { get; private set; }

        // Frames by bitrate (null for free format), padding and length.
        public Dictionary<(int? Bitrate, bool IsPadded, int Length), int> Sizes { get; } = [];

        // Layer III: frames, granules counted per channel as mp3guessenc and EncSpot count them, and
        // those of each BlockKinds entry and with scalefac_scale set.
        public int Layer3Frames { get; private set; }

        public int GranuleChannels { get; private set; }

        public int[] Blocks { get; } = new int[6];

        public int Scaled { get; private set; }

        public bool UsesScfsi { get; private set; }

        public long MainDataBegins { get; private set; }

        public int MaximumMainDataBegin { get; private set; }

        public int[] MinimumGain { get; } = [255, 255];

        public int[] MaximumGain { get; } = [-1, -1];

        // Layers I and II: the ancillary bytes mp3guessenc counts, and the smallest and largest
        // amount of a frame that has any.
        public long Layer12Ancillary { get; private set; }

        public int SmallestLayer12 { get; private set; }

        public int LargestLayer12 { get; private set; }

        // audioEnd: where Layer I or II audio data ends, in bits from the frame's start.
        public void Add(MpegHeader header, int length, Layer3SideInfo? info, int audioEnd)
        {
            ChannelModes[header.ChannelModeBits]++;
            UsesPadding |= header.Padding == 1;
            var key = (header.IsFreeFormat ? (int?)null : header.Bitrate, header.Padding == 1, length);
            Sizes[key] = Sizes.GetValueOrDefault(key) + 1;
            if (header.ChannelModeBits == 1)
            {
                (header.Layer == 3 ? Layer3Extensions : Bounds)[header.ModeExtension]++;
            }

            if (info is not null)
            {
                AddLayer3(info);
            }
            else if (header.Layer != 3 && audioEnd <= length * 8)
            {
                // mp3guessenc (scan_layer__I, scan_layer_II): the whole bytes after the audio data,
                // without the padding slot, in a frame whose audio data fits.
                int bytes = ((length * 8) - audioEnd) / 8 - (header.Padding * header.SlotSize);
                if (bytes > 0)
                {
                    Layer12Ancillary += bytes;
                    SmallestLayer12 = SmallestLayer12 == 0 ? bytes : Math.Min(SmallestLayer12, bytes);
                    LargestLayer12 = Math.Max(LargestLayer12, bytes);
                }
            }
        }

        private void AddLayer3(Layer3SideInfo info)
        {
            Layer3Frames++;
            int begin = info.MainDataBegin.Value;
            MainDataBegins += begin;
            MaximumMainDataBegin = Math.Max(MaximumMainDataBegin, begin);
            UsesScfsi |= info.Scfsi.Any(s => s.Value != 0);
            foreach (Layer3Granule[] row in info.Granules)
            {
                for (int ch = 0; ch < row.Length; ch++)
                {
                    Layer3Granule g = row[ch];
                    GranuleChannels++;
                    Blocks[BlockKind(g)]++;
                    Scaled += g.ScalefacScale.Value;
                    MinimumGain[ch] = Math.Min(MinimumGain[ch], g.GlobalGain.Value);
                    MaximumGain[ch] = Math.Max(MaximumGain[ch], g.GlobalGain.Value);
                }
            }
        }

        // Long (normal), start, short, mixed (short with mixed_block_flag), stop, and the reserved
        // block type 0 with window switching. mp3guessenc counts start, stop and reserved as
        // "switch" granules, EncSpot as long ones.
        private static int BlockKind(Layer3Granule g) => !g.IsSwitched ? 0 : g.BlockType.Value switch
        {
            1 => 1,
            2 => g.MixedBlock.Value == 1 ? 3 : 2,
            3 => 4,
            _ => 5,
        };
    }
}

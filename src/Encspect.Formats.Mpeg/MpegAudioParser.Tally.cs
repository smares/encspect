using Encspect.Core;
using static Encspect.Formats.Mpeg.CodingCategories;

namespace Encspect.Formats.Mpeg;

// The statistics mp3guessenc and EncSpot print about a stream's coding, over a region's complete
// audio frames like the other statistics: channel modes, the stereo coding of all frames and of
// joint stereo frames, Layer III block usage and block sizes, global gain, scfsi, scalefactor
// scaling and main_data_begin, padding, ancillary data and the frame sizes of each bitrate. They
// are counted while the frames are read, so no frame is read twice and nothing is kept per frame;
// the coded bandwidth and the channel bit share are read from the kept audio data of each frame
// when the statistics are built (FrameFigures). The series of the streams are derived from the
// same records and held equal to these counts by CategorySummaryTests.
public sealed partial class MpegAudioParser
{
    // By mode extension: Layer III bit 1 is mid/side stereo, bit 0 intensity stereo (ISO/IEC 11172-3
    // 2.4.2.3).
    private static readonly string[] JointStereoModes = ["Left/right stereo", "Intensity stereo", "Mid/side stereo", "Mid/side and intensity stereo"];

    private static void AddTally(Node node, RegionState region, long start, long length, long audioBytes, long ticks)
    {
        FrameTally tally = region.Tally;
        Node modes = node.Add("Channel Modes", start, length, NodeValue.Integer(tally.ChannelModes.Count(c => c > 0), "modes", "mode"), Explain.ChannelModes, Topics.ChannelModes);
        int frames = tally.ChannelModes.Sum();
        for (int mode = 0; mode < 4; mode++)
        {
            if (tally.ChannelModes[mode] > 0)
            {
                modes.Add(MpegHeader.ChannelModes[mode], start, length, Share(FramesText(tally.ChannelModes[mode]), tally.ChannelModes[mode], frames), Explain.ChannelModeCount);
            }
        }

        region.Nodes.ChannelModes = modes;
        Node coding = node.Add("Stereo Coding", start, length, NodeValue.Integer(frames, "frames", "frame"), Explain.StereoCoding, Topics.JointStereo);
        for (int code = 0; code < StereoCodings.Length; code++)
        {
            if (tally.StereoCodings[code] > 0)
            {
                coding.Add(StereoCodings[code].Name, start, length, Share(FramesText(tally.StereoCodings[code]), tally.StereoCodings[code], frames), StereoCodings[code].Description);
            }
        }

        region.Nodes.StereoCoding = coding;
        region.Nodes.JointStereoModes = AddExtensions("Joint Stereo Modes", tally.Layer3Extensions, e => JointStereoModes[e], Explain.JointStereoModes,
            e => JointCodings[JointCodeOfExtension[e]].Description, Topics.JointStereo);
        region.Nodes.IntensityStereoBounds = AddExtensions("Intensity Stereo Bounds", tally.Bounds, e => BoundCategories[e].Name, Explain.IntensityBounds,
            _ => Explain.IntensityBound, Topics.IntensityStereo);

        if (tally.Layer3Frames > 0)
        {
            Node blocks = node.Add("Block Usage", start, length, NodeValue.Integer(tally.GranuleChannels, "granules", "granule"), Explain.BlockUsage, Topics.BlockSwitching);
            for (int kind = 0; kind < BlockKindNames.Length; kind++)
            {
                if (tally.Blocks[kind] > 0)
                {
                    blocks.Add(BlockKindNames[kind], start, length, Share(GranulesText(tally.Blocks[kind]), tally.Blocks[kind], tally.GranuleChannels), kind == 3 ? Explain.BlockKindMixed : Explain.BlockKind);
                }
            }

            region.Nodes.BlockUsage = blocks;
            Node sizes = node.Add("Block Sizes", start, length, NodeValue.Integer(tally.GranuleChannels, "granules", "granule"), Explain.BlockSizes, Topics.BlockSwitching);
            int[] bySize = new int[SizeNames.Length];
            for (int kind = 0; kind < BlockKindNames.Length; kind++)
            {
                bySize[SizeOfKind[kind]] += tally.Blocks[kind];
            }

            string[] sizeDescriptions = [Explain.SizeLong, Explain.SizeShort, Explain.SizeMixed, Explain.SizeReserved];
            for (int size = 0; size < SizeNames.Length; size++)
            {
                if (bySize[size] > 0)
                {
                    sizes.Add(SizeNames[size], start, length, Share(GranulesText(bySize[size]), bySize[size], tally.GranuleChannels), sizeDescriptions[size]);
                }
            }

            region.Nodes.BlockSizes = sizes;

            Node gain = node.Add(new Node("Global Gain", start, length) { Description = Explain.GainRange, Topic = Topics.GlobalGain });
            for (int ch = 0; ch < 2; ch++)
            {
                if (tally.MaximumGain[ch] >= 0)
                {
                    Node channel = gain.Add(new Node($"Channel {ch}", start, length) { Description = Explain.GainChannel });
                    channel.Add("Minimum", start, length, NodeValue.Integer(tally.MinimumGain[ch]), Explain.GainMinimum);
                    channel.Add("Maximum", start, length, NodeValue.Integer(tally.MaximumGain[ch]), Explain.GainMaximum);
                }
            }
        }

        if (tally.Bandwidths.Count > 0)
        {
            Node bandwidth = node.Add(new Node("Coded Bandwidth", start, length) { Description = Explain.CodedBandwidth, Topic = Topics.CodedBandwidth });
            bandwidth.Add("Lowest", start, length, NodeValue.Integer(tally.Bandwidths.Keys.First(), "Hz"), Explain.BandwidthLowest);
            bandwidth.Add("Most Common", start, length, NodeValue.Integer(tally.Bandwidths.MaxBy(b => b.Value).Key, "Hz"), Explain.BandwidthMostCommon);
            bandwidth.Add("Highest", start, length, NodeValue.Integer(tally.Bandwidths.Keys.Last(), "Hz"), Explain.BandwidthHighest);
            region.Nodes.CodedBandwidth = bandwidth;
        }

        if (tally.ShareFrames > 0)
        {
            Node share = node.Add(new Node("Channel Bit Share", start, length) { Description = Explain.ChannelBitShare, Topic = Topics.ChannelModes });
            share.Add("Lowest", start, length, NodeValue.Decimal(tally.LowestShare / 10.0, 1, "%"), Explain.ShareLowest);
            share.Add("Overall", start, length, NodeValue.Decimal(ShareTenths(tally.FirstBits, tally.BothBits) / 10.0, 1, "%"), Explain.ShareOverall);
            share.Add("Highest", start, length, NodeValue.Decimal(tally.HighestShare / 10.0, 1, "%"), Explain.ShareHighest);
            region.Nodes.ChannelBitShare = share;
        }

        // Layer III's ancillary segments follow mp3guessenc's scan (AncillaryScan); Layers I and II
        // count what mp3guessenc counts after the audio data.
        AncillaryScan? scan = region.Ancillary;
        long ancillary = (scan?.AudioBytes ?? 0) + tally.Layer12Ancillary;
        Node data = node.Add("Ancillary Data", start, length, Bytes(ancillary), Explain.AncillaryTotal, Topics.AncillaryData);
        data.Add("Share", start, length, NodeValue.Decimal(RoundHalfEven((Int128)ancillary * 1000, audioBytes) / 10.0, 1, "%"), Explain.AncillaryShare);
        data.Add("Data Rate", start, length, NodeValue.Decimal(RoundHalfEven((Int128)ancillary * 8 * Clock * 10, (Int128)ticks * 1000) / 10.0, 1, "kbps"), Explain.AncillaryRate);
        if (ancillary > 0)
        {
            long smallest = scan?.SmallestSegment ?? 0;
            smallest = smallest == 0 ? tally.SmallestLayer12 : tally.SmallestLayer12 == 0 ? smallest : Math.Min(smallest, tally.SmallestLayer12);
            data.Add("Smallest Nonzero Amount", start, length, Bytes(smallest), Explain.AncillarySmallest);
            data.Add("Largest Amount", start, length, Bytes(Math.Max(scan?.LargestSegment ?? 0, tally.LargestLayer12)), Explain.AncillaryLargest);
        }

        node.Add("UsesPadding", start, length, NodeValue.Boolean(tally.UsesPadding), Explain.UsesPadding, Topics.Padding);
        if (tally.Layer3Frames > 0)
        {
            node.Add("UsesScfsi", start, length, NodeValue.Boolean(tally.UsesScfsi), Explain.UsesScfsi, Topics.Scfsi);
            node.Add("UsesScalefactorScaling", start, length, NodeValue.Boolean(tally.Scaled > 0), Explain.UsesScaling, Topics.ScalefacScaleAndPreflag);
            node.Add("Scalefactor Scaling", start, length, NodeValue.Decimal(RoundHalfEven((Int128)tally.Scaled * 1000, tally.GranuleChannels) / 10.0, 1, "%"),
                Explain.ScalingShare, Topics.ScalefacScaleAndPreflag);
            node.Add("Average Main Data Begin", start, length, NodeValue.Decimal(RoundHalfEven((Int128)tally.MainDataBegins * 10, tally.Layer3Frames) / 10.0, 1, "bytes"),
                Explain.AverageMainDataBegin, Topics.BitReservoir);
            node.Add("Maximum Main Data Begin", start, length, NodeValue.Integer(tally.MaximumMainDataBegin, "bytes"), Explain.MaximumMainDataBegin, Topics.BitReservoir);
        }

        Node? AddExtensions(string name, int[] counts, Func<int, string> label, string description, Func<int, string> entryDescription, string topic)
        {
            int joint = counts.Sum();
            if (joint == 0)
            {
                return null;
            }

            Node parent = node.Add(name, start, length, NodeValue.Integer(joint, "frames", "frame"), description, topic);
            for (int e = 0; e < 4; e++)
            {
                if (counts[e] > 0)
                {
                    parent.Add(label(e), start, length, Share(FramesText(counts[e]), counts[e], joint), entryDescription(e));
                }
            }

            return parent;
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
                    string.Join(", ", sizes.Select(s => $"{FramesText(s.Value)} of {Number(s.Key.Length)} bytes")), padded ? Explain.Padded : Explain.Unpadded, Topics.Padding);
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

        // All frames by StereoCode.
        public int[] StereoCodings { get; } = new int[6];

        // Layers I and II: frames by coded bandwidth in Hz.
        public SortedDictionary<int, int> Bandwidths { get; } = [];

        // Stereo frames with a share: the first channel's bits and both channels' in all of them, and the smallest and largest
        // share of one frame in tenths of a percent.
        public int ShareFrames { get; private set; }

        public long FirstBits { get; private set; }

        public long BothBits { get; private set; }

        public int LowestShare { get; private set; } = int.MaxValue;

        public int HighestShare { get; private set; } = -1;

        public bool UsesPadding { get; private set; }

        // Frames by bitrate (null for free format), padding and length.
        public Dictionary<(int? Bitrate, bool IsPadded, int Length), int> Sizes { get; } = [];

        // Layer III: frames, granules counted per channel as mp3guessenc and EncSpot count them, and
        // those of each block kind (CodingCategories.BlockKindNames) and with scalefac_scale set.
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
            StereoCodings[StereoCode(header)]++;
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
                // Clean-room rewrite pending: implement from the rules in docs/formats/mpeg-audio.md, "Ancillary data".
                throw new NotImplementedException();
            }
        }

        public void AddFigure(FrameFigures.Figure figure)
        {
            if (figure.HasBandwidth)
            {
                Bandwidths[figure.Bandwidth] = Bandwidths.GetValueOrDefault(figure.Bandwidth) + 1;
            }

            if (figure.HasShare)
            {
                long both = figure.FirstBits + figure.SecondBits;
                int tenths = ShareTenths(figure.FirstBits, both);
                ShareFrames++;
                FirstBits += figure.FirstBits;
                BothBits += both;
                LowestShare = Math.Min(LowestShare, tenths);
                HighestShare = Math.Max(HighestShare, tenths);
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

using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Dts;

// DTS handed over by a container, such as MP4's dtsc, dtsh, dtsl and dtse sample entries (ETSI TS 102 114 Annex E): one packet per
// sample, which holds one access unit, a core frame and its extension substreams (E.3); the DTSSpecificBox ('ddts', E.2.2.3) comes as
// the configuration and is read field by field and compared with the frames. The ID is named after the coding, "dts", not after
// MP4's codes for it. A container that holds the stream in one piece, such as a .dtshd file, hands over a payload, which is read as a raw
// file is. FFmpeg's MP4 muxer writes DTS as an 'mp4a' entry with an esds box of object type 0xA9, which routes here
// without a configuration.
public sealed class DtsCodecParser : ICodecParser
{
    public const string Dts = "dts";

    public string Id => Dts;

    public bool Recognizes(ByteReader payload) => DtsParser.StartsWithChain(payload);

    public void Parse(CodecContext context)
    {
        Node parent = context.Parent;
        var analysis = new DtsAnalysis(context);
        if (context.Payload is { Length: > 0 } stream)
        {
            // A DTS stream in one piece (a .dtshd file's STRMDATA chunk) is read as a .dts file is: frames, gaps and the search for sync.
            DtsParser.ParseSegment(analysis, new BlockCache(stream), parent, stream.Start, stream.End);
            analysis.Finish(context.EncoderClaims);
            return;
        }

        var region = new DtsAnalysis.Region(0);
        analysis.Regions.Add(region);
        var observed = new Observed();
        int index = 0;
        foreach (MappedBytes packet in context.Packets ?? [])
        {
            if (index++ % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            ReadPacket(analysis, region, packet, observed);
        }

        region.End = analysis.Frames.Count;
        DtsRecords records = analysis.Records;
        region.Node = parent.Add(new Node("DTS", parent.Offset, parent.Length,
            context.Configuration is { AreSamplesEncrypted: true } ? NodeValue.Text("frames not read: encrypted") : NodeValue.Integer(analysis.Frames.Count, "frames", "frame"),
            () => new BlockedChildren(region.Leading, region.End.Value - region.First, i => records.FrameNode(region.First + i)))
        {
            Kind = NodeKind.Stream,
            Ranges = parent.Ranges,
            Description = Explain.Stream,
            Topic = Topics.CoreFrames,
        });

        if (context.Configuration is { } configuration && configuration.Parent.Name == "ddts")
        {
            Configuration(analysis, configuration, observed);
        }

        analysis.Finish(context.EncoderClaims);
    }

    // The access unit of one sample, from its start.
    private static void ReadPacket(DtsAnalysis analysis, DtsAnalysis.Region region, MappedBytes packet, Observed observed)
    {
        var source = new BufferSource(packet.Data.ToArray(), packet.OffsetOf(0));
        long position = source.Start;
        while (position < source.End)
        {
            if (Framing.UnitAt(source, position, source.End, false) is not { } unit)
            {
                long offset = position;
                long rest = source.End - position;
                analysis.Findings.Add(Severity.Warning, "dts.sample-not-frame", () => new Node("Gap", offset, rest), count =>
                    $"{(count == 1 ? "A sample holds" : $"{count} samples hold")} bytes that are no core frame or extension substream where one should start, first at 0x{offset:X}.");
                analysis.Frames.Add(DtsAnalysis.Frame.Gap(offset, rest));
                return;
            }

            long stop = Math.Min(position + unit.Length, source.End);
            analysis.AddUnit(region, DtsParser.LayoutOf(position, stop, unit, false, position + unit.Length > source.End), source);
            observed.Add(analysis.LastFacts, (int)(stop - position));
            position = stop;
        }
    }

    // ETSI TS 102 114 E.2.2.3.1: the DTSSpecificBox, read field by field into the box's node and compared with what the frames hold.
    private static void Configuration(DtsAnalysis analysis, CodecConfiguration configuration, Observed observed)
    {
        MappedBytes data = configuration.Data;
        Node box = configuration.Parent;
        int position = 0;

        int Read(string name, int width, string description, Func<int, NodeValue>? value = null)
        {
            if (position + width > data.Length * 8)
            {
                throw new EndOfStreamException();
            }

            long read = 0;
            for (int bit = position; bit < position + width; bit++)
            {
                read = (read << 1) | (uint)((data.Span[bit >> 3] >> (7 - (bit & 7))) & 1);
            }

            int result = (int)read;
            box.Add(Node.FromBits(name, data, position, position + width, value is null ? NodeValue.Integer(result) : value(result), description: description, topic: Topics.ExtensionSubstream));
            position += width;
            return result;
        }

        Node Last() => box.Children[^1];
        void Differs(string field, string stated, string coded) =>
            analysis.Findings.AddNow(Severity.Warning, "dts.config-differs",
                $"The ddts box gives {field} {stated}, but the frames hold {coded}; players that set up their decoder from the box and those that read the frames differ.", Last());

        try
        {
            int rate = Read("Sampling Frequency", 32, Explain.DdtsSamplingFrequency, v => NodeValue.Integer(v, "Hz"));
            if (observed.HighestRate > 0 && rate != observed.HighestRate)
            {
                Differs("DTSSamplingFrequency", $"{rate} Hz", $"{observed.HighestRate} Hz as the highest rate");
            }

            int maxBitrate = Read("Maximum Bitrate", 32, Explain.DdtsMaxBitrate, v => NodeValue.Integer(v, "bit/s"));
            int avgBitrate = Read("Average Bitrate", 32, Explain.DdtsAvgBitrate, v => NodeValue.Integer(v, "bit/s"));
            if (maxBitrate != 0 && observed.PeakBitrate > maxBitrate * 1.001)
            {
                Differs("maxBitrate", $"{maxBitrate} bit/s", $"a unit at {observed.PeakBitrate:F0} bit/s");
            }

            if (avgBitrate != 0 && observed.AverageBitrate > 0 && Math.Abs(observed.AverageBitrate - avgBitrate) > Math.Max(1000, avgBitrate * 0.02))
            {
                Differs("avgBitrate", $"{avgBitrate} bit/s", $"an average of {observed.AverageBitrate:F0} bit/s");
            }

            Read("PCM Sample Depth", 8, Explain.DdtsDepth, v => NodeValue.Integer(v, "bits"));
            int duration = Read("Frame Duration", 2, Explain.DdtsDuration, v => NodeValue.Integer(512 << v, "samples"));
            int construction = Read("Stream Construction", 5, Explain.DdtsConstruction, v => Tables.StreamConstructions.ElementAtOrDefault(v - 1) is var mask and not 0 ? $"{v}: {Tables.ComponentNames(mask)}" : v == 0 ? "0 (none of the listed constructions)" : $"{v} (not listed in Table E-2)");
            int observedConstruction = Tables.StreamConstructionOf(observed.Components);
            if (construction != 0 && observedConstruction != 0 && construction != observedConstruction)
            {
                Differs("StreamConstruction", $"{construction}", $"{observedConstruction} ({Tables.ComponentNames(observed.Components)})");
            }

            if (observed.SamplesPerUnit > 0 && observed.HighestRate > 0 && observed.CoreRate > 0)
            {
                long expected = (long)observed.SamplesPerUnit * observed.HighestRate / observed.CoreRate;
                if (expected != 512 << duration)
                {
                    Differs("FrameDuration", $"{Counts.Of(512 << duration, "sample", "samples")}", $"{Counts.Of(expected, "sample", "samples")}");
                }
            }

            int lfe = Read("HasCoreLfe", 1, Explain.DdtsLfe, v => NodeValue.Boolean(v != 0));
            if (observed.HasCore && observed.CoreHasLfe != (lfe != 0))
            {
                Differs("CoreLFEPresent", lfe.ToString(CultureInfo.InvariantCulture), observed.CoreHasLfe ? "1" : "0");
            }

            int layout = Read("Core Layout", 6, Explain.DdtsLayout, v => v == 31 ? "31 (use ChannelLayout)" : $"{v}: {(v is 0 or 2 or 4 or 5 or 6 or 7 or 8 or 9 ? Tables.ArrangementShort(v) : "not listed in Table E-3")}");
            if (observed.HasCore && layout != 31 && layout != observed.Amode)
            {
                Differs("CoreLayout", layout.ToString(CultureInfo.InvariantCulture), observed.Amode.ToString(CultureInfo.InvariantCulture));
            }

            int size = Read("Core Size", 14, Explain.DdtsCoreSize, v => NodeValue.Integer(v, "bytes"));
            if (observed.HasCore && size != 0 && observed.CoreBytes > 0 && size != observed.CoreBytes)
            {
                Differs("CoreSize", $"{Counts.Of(size, "byte", "bytes")}", $"{Counts.Of(observed.CoreBytes, "byte", "bytes")}");
            }

            Read("HasStereoDownmix", 1, Explain.DdtsDownmix, v => NodeValue.Boolean(v != 0));
            Read("Representation Type", 3, Explain.DdtsRepresentation);
            Read("Channel Layout", 16, Explain.DdtsChannelLayout, v => NodeValue.Hex(v, 4));
            int multi = Read("HasMultipleAssets", 1, Explain.DdtsMultiAsset, v => NodeValue.Boolean(v != 0));
            if (observed.Assets > 0 && (multi != 0) != (observed.Assets > 1))
            {
                Differs("MultiAssetFlag", multi.ToString(CultureInfo.InvariantCulture), observed.Assets > 1 ? "1" : "0");
            }

            Read("HasLbrDurationModifier", 1, Explain.DdtsLbrDuration, v => NodeValue.Boolean(v != 0));
            Read("HasReservedBox", 1, Explain.DdtsReservedBox, v => NodeValue.Boolean(v != 0));
            int reserved = Read("Reserved", 5, Explain.DdtsReserved);
            if (reserved != 0)
            {
                analysis.Findings.AddNow(Severity.Warning, "dts.config-reserved", "Reserved bits of the ddts box are not 0, as ETSI TS 102 114 E.2.2.3.2 requires.", Last());
            }

            if (position < data.Length * 8)
            {
                box.Add(Node.FromBits("Reserved Box", data, position, data.Length * 8, NodeValue.Integer((data.Length * 8) - position, "bits"), description: Explain.DdtsTrailing));
            }
        }
        catch (EndOfStreamException)
        {
            analysis.Findings.AddNow(Severity.Warning, "dts.config-truncated", "The ddts box ends inside a field.", box);
        }
    }

    // What the units hold that the configuration states.
    private sealed class Observed
    {
        private long _bytes;
        private long _samples;
        private int _units;

        public int HighestRate { get; private set; }

        public int CoreRate { get; private set; }

        public int SamplesPerUnit { get; private set; }

        public int Components { get; private set; }

        public bool HasCore { get; private set; }

        public bool CoreHasLfe { get; private set; }

        public int Amode { get; private set; }

        public int CoreBytes { get; private set; }

        public int Assets { get; private set; }

        public double PeakBitrate { get; private set; }

        public double AverageBitrate => _samples > 0 && HighestRate > 0 && CoreRate > 0 ? _bytes * 8.0 * CoreRate / _samples : 0;

        public void Add(UnitFacts facts, int length)
        {
            if (_units++ == 0)
            {
                HasCore = facts.HasCore;
                if (facts.HasCore)
                {
                    CoreRate = facts.Core.SampleRate;
                    SamplesPerUnit = facts.Core.Samples;
                    CoreHasLfe = facts.Core.Lff is 1 or 2;
                    Amode = facts.Core.Amode;
                    CoreBytes = facts.Form.Is14Bit() ? 0 : facts.Core.PackedLength;
                }

                Assets = facts.Substreams.Count > 0 ? facts.Substreams[0].Assets.Count : 0;
            }

            Components |= facts.Components;
            HighestRate = Math.Max(HighestRate, facts.HasCore ? facts.Core.SampleRate : 0);
            foreach (AssetFacts asset in facts.Substreams.SelectMany(s => s.Assets))
            {
                HighestRate = Math.Max(HighestRate, StreamKey.RateOf(asset));
            }

            if (facts.Extension == CoreExtension.X96 && facts.HasCore)
            {
                HighestRate = Math.Max(HighestRate, facts.Core.SampleRate * 2);
            }

            if (facts.HasCore)
            {
                _bytes += length;
                _samples += facts.Core.Samples;
                PeakBitrate = Math.Max(PeakBitrate, length * 8.0 * facts.Core.SampleRate / facts.Core.Samples);
            }
        }
    }
}

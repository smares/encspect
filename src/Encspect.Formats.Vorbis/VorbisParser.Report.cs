using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Vorbis;

public sealed partial class VorbisParser
{
    // The vendor string libvorbis writes into every comment header it encodes (lib/info.c,
    // ENCODE_VENDOR_STRING, or the string _vorbis_pack_comment writes before 1.2.2), by release, from
    // the tags of the libvorbis repository. 1.1.1 and 1.1.2 write the same string; 1.2.1 has no tag.
    private static readonly Dictionary<string, string> LibvorbisReleases = new(StringComparer.Ordinal)
    {
        ["Xiph.Org libVorbis I 20020717"] = "1.0",
        ["Xiph.Org libVorbis I 20030909"] = "1.0.1",
        ["Xiph.Org libVorbis I 20040629"] = "1.1.0",
        ["Xiph.Org libVorbis I 20050304"] = "1.1.1 or 1.1.2",
        ["Xiph.Org libVorbis I 20070622"] = "1.2.0",
        ["Xiph.Org libVorbis I 20090624"] = "1.2.2",
        ["Xiph.Org libVorbis I 20090709"] = "1.2.3",
        ["Xiph.Org libVorbis I 20100325 (Everywhere)"] = "1.3.1",
        ["Xiph.Org libVorbis I 20101101 (Schaufenugget)"] = "1.3.2",
        ["Xiph.Org libVorbis I 20120203 (Omnipresent)"] = "1.3.3",
        ["Xiph.Org libVorbis I 20140122 (Turpakäräjiin)"] = "1.3.4",
        ["Xiph.Org libVorbis I 20150105 (⛄⛄⛄⛄)"] = "1.3.5",
        ["Xiph.Org libVorbis I 20180316 (Now 100% fewer shells)"] = "1.3.6",
        ["Xiph.Org libVorbis I 20200704 (Reducing Environment)"] = "1.3.7",
    };

    // libvorbis' vendor string of a build the table does not list, such as a release candidate.
    [GeneratedRegex(@"^Xiph\.Org libVorbis I (\d{8})")]
    private static partial Regex LibvorbisVendor();

    // The vendor string of each aoTuV release (Aoyumi's tuned libvorbis), from lib/info.c of its
    // source releases on aoTuV's site (AO-Yumi/aotuv_web) and of the 2018 and 2020 rebuilds of
    // beta 6.03 (AO-Yumi/vorbis_aotuv); a1 to a3 and b1 have no source left, and these come from
    // their reference binaries there. aotuv_tool.py --vendors checks the table against the sources.
    // beta 6.03 of 2011, 2014 and 2015 write the same string; the rebuilds name the libvorbis
    // they are based on. Rarewares' build of oggenc2.88 on beta 6.03 (2020) writes its own.
    private static readonly Dictionary<string, string> AotuvReleases = new(StringComparer.Ordinal)
    {
        ["AO; aoTuV a1 (based on Xiph.org's 1.0.1)"] = "aoTuV a1",
        ["AO; aoTuV a2 (based on Xiph.org's 1.0.1)"] = "aoTuV a2",
        ["AO; aoTuV a3 (based on Xiph.org's 1.0.1)"] = "aoTuV a3",
        ["AO; aoTuV b1 (based on Xiph.Org's 1.0.1)"] = "aoTuV b1",
        ["AO; aoTuV b1a (based on Xiph.Org's 1.0.1)"] = "aoTuV b1a",
        ["AO; aoTuV b2 [20040420]  (based on Xiph.Org's 1.0.1)"] = "aoTuV b2 (20040420)",
        ["AO; aoTuV b3 [20041120] (based on Xiph.Org's libVorbis)"] = "aoTuV b3 (20041120)",
        ["AO; aoTuV b4 [20050617] (based on Xiph.Org's libVorbis)"] = "aoTuV b4 (20050617)",
        ["AO; aoTuV b4a [20051105] (based on Xiph.Org's libVorbis)"] = "aoTuV b4.5 (20051105)",
        ["AO; aoTuV b4b [20051117] (based on Xiph.Org's libVorbis)"] = "aoTuV b4.51 (20051117)",
        ["AO; aoTuV r1 [20051117] (based on Xiph.Org's libVorbis)"] = "aoTuV r1 (20051117)",
        ["AO; aoTuV b5 [20061024] (based on Xiph.Org's libVorbis)"] = "aoTuV b5 (20061024)",
        ["AO; aoTuV b5a [20080330] (based on Xiph.Org's libVorbis)"] = "aoTuV b5.5 (20080330)",
        ["AO; aoTuV b5b [20081209] (based on Xiph.Org's libVorbis)"] = "aoTuV b5.6 (20081209)",
        ["AO; aoTuV b5c [20081215] (based on Xiph.Org's libVorbis)"] = "aoTuV b5.61 (20081215)",
        ["AO; aoTuV b5d [20090301] (based on Xiph.Org's libVorbis)"] = "aoTuV b5.7 (20090301)",
        ["AO; aoTuV [20110221] (based on Xiph.Org's libVorbis)"] = "aoTuV b6 (20110221)",
        ["AO; aoTuV [20110223] (based on Xiph.Org's libVorbis)"] = "aoTuV b6.01 (20110223)",
        ["AO; aoTuV [20110227] (based on Xiph.Org's libVorbis)"] = "aoTuV b6.02 (20110227)",
        ["AO; aoTuV [20110424] (based on Xiph.Org's libVorbis)"] = "aoTuV b6.03 (20110424)",
        ["AO; aoTuV [20110424] (based on libvorbis 1.3.6)"] = "aoTuV b6.03 (20110424, 2018 build on libvorbis 1.3.6)",
        ["AO; aoTuV [20110424] (based on libvorbis 1.3.7)"] = "aoTuV b6.03 (20110424, 2020 build on libvorbis 1.3.7)",
        ["AO; aoTuV 6.03 (2020) (based on libVorbis 1.3.7)"] = "aoTuV b6.03 (2020, Rarewares build)",
    };

    [GeneratedRegex(@"^AO; aoTuV ?(.*)$")]
    private static partial Regex AotuvVendor();

    // Lancer, an optimized aoTuV, writes a vendor string of its own: Rarewares' SSE2 build of
    // oggenc2.88 on aoTuV b6.03 (2020) writes "BS; LancerMod(SSE2) (based on aoTuV 6.03 (2020))".
    private const string LancerSse2 = "BS; LancerMod(SSE2) (based on aoTuV 6.03 (2020))";

    [GeneratedRegex(@"^BS; LancerMod\((\w+)\) \(based on (aoTuV .*)\)$")]
    private static partial Regex LancerVendor();

    // ffmpeg's ENCODER field after libvorbis or its native encoder: "Lavc60.31.102 libvorbis".
    [GeneratedRegex(@"^Lavc\S* (lib)?vorbis$")]
    private static partial Regex FfmpegEncoder();

    private sealed partial class StreamState
    {
        // The comment of the stream itself: only its claims count.
        private List<EncoderClaim> CommentClaims() => _comment is { } tag
            ? context.EncoderClaims.Where(c => c.Source.Offset >= tag.Offset && c.Source.End <= tag.End).ToList()
            : [];

        // The encoder, from the comment's vendor string, which the library that encoded the audio
        // writes (oggenc leaves libvorbis' vendor string), and an ENCODER field that is a libavcodec encoder ID. ffmpeg
        // writes its own version ("Lavf60.16.100") as the vendor, which names the writer (AddWriter), and
        // "Lavc60.31.102 libvorbis" or, for its native encoder, "Lavc60.31.102 vorbis" as ENCODER when it encodes.
        private Node? AddEncoder()
        {
            List<EncoderClaim> encoders = [.. ClaimRoles.Encoders(CommentClaims())];
            EncoderClaim? vendor = encoders.FirstOrDefault(c => c.Source.Name == "Vendor");
            EncoderClaim? field = encoders.FirstOrDefault(c => c.Source.Name != "Vendor");

            var evidence = new List<Node>();
            string? name = null;
            Confidence confidence = Confidence.Medium;
            if (field is not null && FfmpegEncoder().IsMatch(field.Text))
            {
                name = $"FFmpeg ({field.Text})";
                evidence.Add(Evidence(field.Source, $"Vorbis comment ENCODER field \"{field.Text}\", which ffmpeg writes when it encodes"));
            }
            else if (vendor is not null && LibvorbisReleases.TryGetValue(vendor.Text, out string? release))
            {
                name = $"libvorbis {release}";
                confidence = Confidence.High;
                evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", which libvorbis {release} writes"));
            }
            else if (vendor is not null && AotuvReleases.TryGetValue(vendor.Text, out string? aotuv))
            {
                name = aotuv;
                confidence = Confidence.High;
                evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", which {aotuv} writes"));
            }
            else if (vendor is not null && LancerVendor().Match(vendor.Text) is { Success: true } lancer)
            {
                name = $"Lancer ({lancer.Groups[1].Value}, based on {lancer.Groups[2].Value})";
                confidence = vendor.Text == LancerSse2 ? Confidence.High : Confidence.Medium;
                evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", which Lancer, an optimized aoTuV, writes"));
            }
            else if (vendor is not null && AotuvVendor().Match(vendor.Text) is { Success: true } other)
            {
                string version = other.Groups[1].Value;
                name = version.Length is > 0 and <= 60 ? $"aoTuV ({version})" : "aoTuV";
                evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", aoTuV's form, of no release the analyzer knows"));
            }
            else if (vendor is not null && LibvorbisVendor().Match(vendor.Text) is { Success: true } match)
            {
                name = $"libvorbis ({match.Groups[1].Value})";
                evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", libvorbis' form, of no release the analyzer knows"));
            }

            if (name is null)
            {
                return null;
            }

            long start = evidence.Min(e => e.Offset);
            long end = evidence.Max(e => e.End);
            var node = new Node("Encoder", start, end - start, name) { Description = Explain.Encoder, Topic = Topics.EncoderDetection };
            node.Add("Confidence", start, end - start, confidence.ToString(), Explain.Confidence);
            foreach (Node item in evidence)
            {
                node.Add(item);
            }

            return _parent.Add(node);
        }

        // The writer, from the comment's ENCODER field and a vendor string that is a muxer's version.
        private Node? AddWriter()
        {
            List<EncoderClaim> claims = CommentClaims();
            if (_comment is not { } tag || !ClaimRoles.Writers(claims).Any())
            {
                return null;
            }

            Node? writer = WriterDetection.Detect(claims, (name, value, description) => new Node(name, tag.Offset, tag.Length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
                (claim, text, name) => _findings.Add(Severity.Info, "vorbis.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
                isEvidenceAtSource: true,
                format: new WriterDetection.StreamFormat(KnownWriters.Vorbis, "Vorbis", (claim, message, node) => _findings.Add(Severity.Info, "vorbis.writer-other-format", node, _ => message)));
            return writer is null ? null : _parent.Add(writer);
        }

        private static Node Evidence(Node at, string text) => new("Evidence", at.Offset, at.Length, text) { Description = Explain.Evidence };

        // Figures over the audio packets: their blocks, the samples the overlap rule and the granule
        // positions give, the bitrate from the packets' bytes, and the packet sizes per block size.
        // Returns the node the per-second series is shown after.
        private Node? AddStatistics(long samples, long startTrim, long trim)
        {
            if (_packets == 0)
            {
                return null;
            }

            long start = _audioStart;
            long length = _audioEnd - _audioStart;
            long blocks = _longBlocks + _shortBlocks;
            Node node = _parent.Add(new Node("Statistics", start, length,
                HasTime ? $"{Count(_packets, "packet", "packets")}, {(samples / (double)Rate).ToString("F3", CultureInfo.InvariantCulture)} s" : Count(_packets, "packet", "packets"))
            {
                Kind = NodeKind.Statistics,
                Description = Explain.Statistics,
                Topic = Topics.VorbisBlocks,
            });

            Node Add(string name, NodeValue value, string description, string? topic = null) => node.Add(name, start, length, value, description, topic);

            Add("Packets", NodeValue.Integer(_packets), Explain.Packets);
            if (blocks > 0)
            {
                Add("Long Blocks", Share(_longBlocks, blocks, "packet", "packets"), Explain.LongBlocks, Topics.VorbisBlocks);
                Add("Short Blocks", Share(_shortBlocks, blocks, "packet", "packets"), Explain.ShortBlocks, Topics.VorbisBlocks);
            }

            if (HasTime && blocks > 0)
            {
                Add("Decoded Samples", NodeValue.Integer(_decoded, "samples", "sample"), Explain.DecodedSamples, Topics.VorbisBlocks);
                if (_firstOffset is { } offset)
                {
                    Add("Start Offset", NodeValue.Integer(offset, "samples", "sample"), Explain.StartOffset, Topics.GranulePosition);
                    Add("Start Trimming", NodeValue.Integer(startTrim, "samples", "sample"), Explain.StartTrimming, Topics.GranulePosition);
                    Add("End Trimming", NodeValue.Integer(trim, "samples", "sample"), Explain.EndTrimming, Topics.GranulePosition);
                }

                Add("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.Samples, Topics.GranulePosition);
                Add("Duration", NodeValue.Decimal(samples / (double)Rate, 3, "s"), Explain.Duration);
                Add("IsVbr", NodeValue.Boolean(_sizes.Count > 1), Explain.StatIsVbr, Topics.BitrateModes);
                if (_decoded > 0)
                {
                    Add("Average Bitrate", NodeValue.Decimal(_bytes * 8.0 * Rate / _decoded / 1000, 1, "kbps"), Explain.AverageBitrate);
                }

                (double minimum, double maximum) = BitrateRange();
                Add("Minimum Bitrate", NodeValue.Decimal(minimum, 1, "kbps"), Explain.MinimumBitrate);
                Add("Maximum Bitrate", NodeValue.Decimal(maximum, 1, "kbps"), Explain.MaximumBitrate);
            }

            Node sizes = Add("Packet Sizes", Count(_sizes.Count, "size", "sizes"), Explain.PacketSizes);
            for (int kind = 1; kind >= 0; kind--)
            {
                long count = kind == 1 ? _longBlocks : _shortBlocks;
                if (count > 0)
                {
                    double average = _blockBytes[kind] / (double)count;
                    sizes.Add(kind == 1 ? "Long Blocks" : "Short Blocks", start, length,
                        $"{_smallest[kind]} to {_largest[kind]} bytes, {Math.Round(average, 1, MidpointRounding.ToEven).ToString("F1", CultureInfo.InvariantCulture)} on average", Explain.PacketSizesPerBlock);
                }
            }

            if (_setup is { HasModes: true } setup && blocks > 0)
            {
                Node modes = Add("Modes", Count(_modeCounts.Count(c => c > 0), "mode used", "modes used"), Explain.ModeUsage, Topics.VorbisBlocks);
                for (int mode = 0; mode < setup.ModeIsLong!.Length; mode++)
                {
                    if (_modeCounts[mode] > 0)
                    {
                        modes.Add($"Mode {mode} ({(setup.ModeIsLong[mode] ? "long" : "short")} block)", start, length, Share(_modeCounts[mode], blocks, "packet", "packets"), Explain.ModeShare);
                    }
                }
            }

            return sizes;
        }

        // A packet's bitrate: its bits over the samples its block codes, half the block size, since
        // the transform of a block of n samples gives n/2 coefficients per channel.
        private double PacketBitrate(PacketRecord packet) => packet.Length * 8.0 * Rate / (_records.BlockSize(packet) / 2) / 1000;

        private (double Minimum, double Maximum) BitrateRange()
        {
            double minimum = double.MaxValue;
            double maximum = 0;
            for (int kind = 0; kind < 2; kind++)
            {
                if (_largest[kind] == 0 && _smallest[kind] == int.MaxValue)
                {
                    continue;
                }

                int half = (kind == 1 ? _records.Blocksize1 : _records.Blocksize0) / 2;
                minimum = Math.Min(minimum, _smallest[kind] * 8.0 * Rate / half / 1000);
                maximum = Math.Max(maximum, _largest[kind] * 8.0 * Rate / half / 1000);
            }

            return (minimum == double.MaxValue ? 0 : minimum, maximum);
        }

        private static string Share(long count, long total, string singular, string plural) =>
            $"{Count(count, singular, plural)} ({Math.Round(count * 100.0 / total, 1, MidpointRounding.ToEven).ToString("F1", CultureInfo.InvariantCulture)}%)";

        private Node? AddPackets()
        {
            if (_packets == 0)
            {
                return null;
            }

            Records records = _records;
            return _parent.Add(new Node("Audio Packets", _audioStart, _audioEnd - _audioStart, NodeValue.Integer(_packets, "packets", "packet"), () => new BlockedChildren([], records.Packets.Count, i => PacketNode(records, i)))
            {
                Kind = NodeKind.Stream,
                Description = Explain.AudioPackets,
                Topic = Topics.VorbisBlocks,
            });
        }

        // Per packet that codes a block, its bitrate at its offset (0 for one that codes none); per
        // second of audio, the bits of the packets whose blocks start in it over the samples those
        // blocks code, rounded half to even to 0.1 kbps. A stream whose blocks last longer than a
        // second, from a corrupt sample rate, has no per-second series.
        private List<Series> Series(Node? anchor, Node? packets)
        {
            if (!HasTime || _longBlocks + _shortBlocks == 0 || packets is null)
            {
                return [];
            }

            bool perSecond = _records.Blocksize1 / 2 <= Rate;
            int count = _records.Packets.Count;
            double[] offsets = _records.OffsetsForSeries(context.CancellationToken);
            var bitrates = new double[count];
            var seconds = new List<(long Bits, long Samples)>();
            long position = 0;
            for (int i = 0; i < count; i++)
            {
                if (i % 4096 == 0)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }

                PacketRecord packet = _records.Packets[i];
                if (!packet.IsBlock)
                {
                    continue;
                }

                bitrates[i] = PacketBitrate(packet);
                int half = _records.BlockSize(packet) / 2;
                if (perSecond)
                {
                    int second = (int)(position / Rate);
                    while (seconds.Count <= second)
                    {
                        seconds.Add(default);
                    }

                    seconds[second] = (seconds[second].Bits + (packet.Length * 8L), seconds[second].Samples + half);
                }

                position += half;
            }

            double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
            double[] rates = seconds
                .Select(s => s.Samples > 0 ? Math.Round(s.Bits * (double)Rate / s.Samples / 1000, 1, MidpointRounding.ToEven) : 0)
                .ToArray();
            var bitrate = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = SeriesText.Bitrate };
            return perSecond ? [bitrate, new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, anchor) { Description = SeriesText.BitrateOverTime }] : [bitrate];
        }
    }

    // Findings of one kind are reported once per stream with a count, pointing at the first case.
    private sealed class FindingCollector
    {
        private readonly Dictionary<string, (Severity Severity, Node Node, int Count, Func<int, string> Message)> _findings = [];
        private readonly List<string> _order = [];

        public void Add(Severity severity, string code, Node node, Func<int, string> message)
        {
            if (_findings.TryGetValue(code, out var existing))
            {
                _findings[code] = existing with { Count = existing.Count + 1 };
                return;
            }

            _findings[code] = (severity, node, 1, message);
            _order.Add(code);
        }

        public void Flush(AnalysisContext context)
        {
            foreach (string code in _order)
            {
                var entry = _findings[code];
                context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node, entry.Count);
            }
        }
    }
}

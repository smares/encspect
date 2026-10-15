using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Speex;

public sealed partial class SpeexParser
{
    // speexenc.c: snprintf(vendor_string, ..., "Encoded with Speex %s", speex_version).
    [GeneratedRegex(@"^Encoded with Speex (\S+)$")]
    private static partial Regex SpeexencVendor();

    private sealed partial class StreamState
    {
        // The encoder, from the comment's vendor string, an ENCODER field that is a libavcodec encoder ID and the header's version
        // string, which libspeex's speex_init_header writes whatever program encodes: speexenc writes "Encoded with Speex" and the
        // library version as the vendor. ffmpeg writes its own version ("Lavf60.16.100") as the vendor, which names the writer
        // (AddWriter), and, where it encoded, "Lavc60.31.102 libspeex" as the ENCODER field; a remux writes "Lavf" there too.
        private Node? AddEncoder()
        {
            List<EncoderClaim> encoders = [.. ClaimRoles.Encoders(CommentClaims())];
            EncoderClaim? vendor = encoders.FirstOrDefault(c => c.Source.Name == "Vendor");
            EncoderClaim? field = encoders.FirstOrDefault(c => c.Source.Name != "Vendor");
            Node? versionNode = _headerNode?.Children.FirstOrDefault(c => c.Name == "Speex Version");
            string? library = _header?.Version is { Length: > 0 } text ? text : null;

            var evidence = new List<Node>();
            string? name = null;
            Confidence confidence = Confidence.Low;
            if (vendor is not null && SpeexencVendor().Match(vendor.Text) is { Success: true } match)
            {
                string version = match.Groups[1].Value;
                name = $"speexenc {version}";
                confidence = version == library ? Confidence.High : Confidence.Medium;
                evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", which speexenc writes"));
                if (versionNode is not null)
                {
                    evidence.Add(Evidence(versionNode, version == library
                        ? $"Speex header version \"{library}\", which libspeex writes, agrees"
                        : $"Speex header version \"{library}\" differs from the vendor string's"));
                }
            }
            else if (field is not null && field.Text.Contains("libspeex", StringComparison.Ordinal))
            {
                name = $"FFmpeg ({field.Text})";
                confidence = Confidence.Medium;
                evidence.Add(Evidence(field.Source, $"Vorbis comment ENCODER field \"{field.Text}\", which ffmpeg writes when it encodes with libspeex"));
                if (versionNode is not null && library is not null)
                {
                    evidence.Add(Evidence(versionNode, $"Speex header version \"{library}\", which libspeex writes"));
                }
            }
            else if (library is not null && versionNode is not null)
            {
                name = $"libspeex {library}";
                evidence.Add(Evidence(versionNode, $"Speex header version \"{library}\", which libspeex writes whatever program encodes"));
                if (vendor is not null)
                {
                    evidence.Add(Evidence(vendor.Source, $"{vendor.Description} \"{vendor.Text}\" names no encoder"));
                }
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

        // The comment of the stream itself: only its claims count.
        private List<EncoderClaim> CommentClaims() => _comment is { } tag
            ? context.EncoderClaims.Where(c => c.Source.Offset >= tag.Offset && c.Source.End <= tag.End).ToList()
            : [];

        // The writer, from the comment's ENCODER field and a vendor string that is a muxer's version.
        private Node? AddWriter()
        {
            List<EncoderClaim> claims = CommentClaims();
            if (_comment is not { } tag || !ClaimRoles.Writers(claims).Any())
            {
                return null;
            }

            Node? writer = WriterDetection.Detect(claims, (name, value, description) => new Node(name, tag.Offset, tag.Length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
                (claim, text, name) => _findings.Add(Severity.Info, "speex.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
                isEvidenceAtSource: true,
                format: new WriterDetection.StreamFormat(KnownWriters.Speex, "Speex", (claim, message, node) => _findings.Add(Severity.Info, "speex.writer-other-format", node, _ => message)));
            return writer is null ? null : _parent.Add(writer);
        }

        private static Node Evidence(Node at, string text) => new("Evidence", at.Offset, at.Length, text) { Description = Explain.Evidence };

        // Figures over the audio packets, as the MPEG statistics are: counts, the samples the
        // granule positions give, the bitrate from the packets' bytes over the samples they code,
        // the packet sizes and the frames of each submode. Returns the node the per-second series
        // is shown after.
        private Node? AddStatistics(long samples, long trim)
        {
            if (_packets == 0)
            {
                return null;
            }

            long start = _audioStart;
            long length = _audioEnd - _audioStart;
            Node node = _parent.Add(new Node("Statistics", start, length,
                HasTime ? $"{Count(_packets, "packet", "packets")}, {(samples / (double)Rate).ToString("F3", CultureInfo.InvariantCulture)} s" : Count(_packets, "packet", "packets"))
            {
                Kind = NodeKind.Statistics,
                Description = Explain.Statistics,
                Topic = Topics.SpeexModes,
            });

            void Add(string name, NodeValue value, string description, string? topic = null) => node.Add(name, start, length, value, description, topic);

            Add("Packets", NodeValue.Integer(_packets), Explain.Packets);
            Add("Frames", NodeValue.Integer(_frames), Explain.Frames);
            if (HasTime)
            {
                Add("Coded Samples", NodeValue.Integer(_coded, "samples", "sample"), Explain.CodedSamples);
                if (_firstDelay is { } delay)
                {
                    Add("Encoder Delay", NodeValue.Integer(delay, "samples", "sample"), Explain.EncoderDelay, Topics.GranulePosition);
                    Add("End Trimming", NodeValue.Integer(trim, "samples", "sample"), Explain.EndTrimming, Topics.GranulePosition);
                }

                Add("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.Samples, Topics.GranulePosition);
                Add("Duration", NodeValue.Decimal(samples / (double)Rate, 3, "s"), Explain.Duration);
                Add("IsVbr", NodeValue.Boolean(_sizes.Count > 1), Explain.StatIsVbr, Topics.BitrateModes);
                Add("Average Bitrate", NodeValue.Decimal(_bytes * 8.0 * Rate / _coded / 1000, 1, "kbps"), Explain.AverageBitrate);
                Add("Minimum Bitrate", NodeValue.Decimal(PacketBitrate(_sizes.First().Key), 1, "kbps"), Explain.MinimumBitrate);
                Add("Maximum Bitrate", NodeValue.Decimal(PacketBitrate(_sizes.Last().Key), 1, "kbps"), Explain.MaximumBitrate);
            }

            Node distribution = node.Add("Packet Sizes", start, length, NodeValue.Integer(_sizes.Count, "sizes", "size"), Explain.PacketSizes);
            foreach ((int size, long count) in _sizes)
            {
                string bitrate = HasTime ? $", {PacketBitrate(size).ToString("F1", CultureInfo.InvariantCulture)} kbps" : string.Empty;
                distribution.Add($"{size} bytes", start, length, $"{Share(count, _packets, "packet", "packets")}{bitrate}", Explain.PacketSize);
            }

            if (Mode >= 0 && _frames > 0)
            {
                AddSubmodes(node, "Narrowband Submodes", Explain.NarrowbandSubmodes, _narrowband, SpeexFrames.NarrowbandBits, null);
                if (Mode >= 1)
                {
                    AddSubmodes(node, "Wideband Submodes", Explain.WidebandSubmodes, _wideband, SpeexFrames.WidebandBits, "No wideband layer");
                }

                if (Mode >= 2)
                {
                    AddSubmodes(node, "Ultra-Wideband Submodes", Explain.UltraWidebandSubmodes, _ultraWideband, SpeexFrames.UltraWidebandBits, "No ultra-wideband layer");
                }
            }

            if (_inBand > 0)
            {
                Add("In-band Requests", NodeValue.Integer(_inBand), Explain.InBandRequests);
                Add("Stereo Parameters", NodeValue.Integer(_stereo), Explain.StereoParameters);
            }

            return distribution;
        }

        private void AddSubmodes(Node statistics, string name, string description, long[] counts, ReadOnlySpan<int> bits, string? absent)
        {
            int kinds = counts.Count(c => c > 0);
            Node node = statistics.Add(name, statistics.Offset, statistics.Length, NodeValue.Integer(kinds, "submodes", "submode"), description);
            for (int submode = 0; submode < bits.Length; submode++)
            {
                if (counts[submode] > 0)
                {
                    node.Add($"Submode {submode} ({bits[submode]} bits)", statistics.Offset, statistics.Length, Share(counts[submode], _frames, "frame", "frames"), Explain.SubmodeShare);
                }
            }

            if (absent is not null && counts[FrameRecord.Absent] > 0)
            {
                node.Add(absent, statistics.Offset, statistics.Length, Share(counts[FrameRecord.Absent], _frames, "frame", "frames"), Explain.LayerAbsent);
            }
        }

        // A packet's bytes over the samples it codes, in kbps.
        private double PacketBitrate(int bytes) => bytes * 8.0 * Rate / SamplesPerPacket / 1000;

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
                Topic = Topics.SpeexModes,
            });
        }

        // Per packet, its bitrate at its offset; per second of coded audio, the bits of the packets
        // that start in it over their duration, rounded half to even to 0.1 kbps. A header whose
        // sample rate makes a packet last longer than a second has no per-second series, so a
        // corrupt rate cannot make one of billions of seconds.
        private List<Series> Series(Node? distribution, Node? packets)
        {
            if (!HasTime || _packets == 0 || packets is null)
            {
                return [];
            }

            bool perSecond = SamplesPerPacket <= Rate;

            int count = _records.Packets.Count;
            double[] offsets = _records.OffsetsForSeries(context.CancellationToken);
            var bitrates = new double[count];
            var seconds = new List<(long Bits, long Packets)>();
            long perPacket = SamplesPerPacket;
            for (int i = 0; i < count; i++)
            {
                if (i % 4096 == 0)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }

                PacketRecord packet = _records.Packets[i];
                bitrates[i] = PacketBitrate(packet.Length);
                if (!perSecond)
                {
                    continue;
                }

                int second = (int)(i * perPacket / Rate);
                while (seconds.Count <= second)
                {
                    seconds.Add(default);
                }

                seconds[second] = (seconds[second].Bits + (packet.Length * 8L), seconds[second].Packets + 1);
            }

            double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
            double[] rates = seconds
                .Select(s => s.Packets > 0 ? Math.Round(s.Bits * (double)Rate / (s.Packets * perPacket) / 1000, 1, MidpointRounding.ToEven) : 0)
                .ToArray();
            var bitrate = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = SeriesText.Bitrate };
            return perSecond ? [bitrate, new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, distribution) { Description = SeriesText.BitrateOverTime }] : [bitrate];
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

using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Opus;

public sealed partial class OpusParser
{
    // opusenc's ENCODER field (opus-tools, opusenc.c): "opusenc from opus-tools 0.2".
    [GeneratedRegex(@"^opusenc from opus-tools (\S+)")]
    private static partial Regex OpusencEncoder();

    // libopus' version string (opus_get_version_string), which libopusenc's vendor string starts
    // with: "libopus 1.4, libopusenc 0.2.1".
    [GeneratedRegex(@"^libopus (\S+?)(,|$)")]
    private static partial Regex LibopusVendor();

    // ffmpeg's ENCODER field after libopus or its native encoder: "Lavc60.31.102 libopus".
    [GeneratedRegex(@"^Lavc(\d\S*)? (lib)?opus$")]
    private static partial Regex FfmpegEncoder();

    private sealed partial class StreamState
    {
        // The encoder, from the comment's vendor string (libopusenc writes the version of libopus into it) and an ENCODER field that
        // is a libavcodec encoder ID: ffmpeg writes "Lavc60.31.102 libopus" or, for its native encoder, "Lavc60.31.102 opus" when it
        // encodes, with its own version ("Lavf60.16.100") as the vendor, which names the writer (AddWriter). RFC 7845 section 5.2
        // has the vendor string "identify the codec encoder and encapsulation implementations" and applications use the ENCODER
        // comment "to identify themselves": opusenc's "opusenc from opus-tools" is the writer's.
        private Node? AddEncoder()
        {
            List<EncoderClaim> encoders = [.. ClaimRoles.Encoders(CommentClaims())];
            EncoderClaim? vendor = encoders.FirstOrDefault(c => c.Source.Name == "Vendor");
            List<EncoderClaim> fields = encoders.Where(c => c.Source.Name != "Vendor").ToList();
            EncoderClaim? field = fields.FirstOrDefault(c => FfmpegEncoder().IsMatch(c.Text));
            Match? library = vendor is null ? null : LibopusVendor().Match(vendor.Text);

            var evidence = new List<Node>();
            string? name = null;
            Confidence confidence = Confidence.Medium;
            // One rule for a libavcodec encoder ID in the ENCODER field: one of this codec names an encoder (ffmpeg's) unless stream evidence names a
            // real encoder first; one of another codec never becomes the encoder, stays as evidence under the Encoder node where there is one and is reported.
            bool isOwn = field is not null;
            if (vendor is not null && library is { Success: true })
            {
                name = $"libopus {library.Groups[1].Value}";
                evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", which names libopus' version"));
            }
            else if (isOwn)
            {
                name = $"FFmpeg ({field!.Text})";
                evidence.Add(Evidence(field.Source, $"Vorbis comment ENCODER field \"{field.Text}\", which ffmpeg writes when it encodes"));
            }

            foreach (EncoderClaim claim in fields)
            {
                if (FfmpegEncoder().IsMatch(claim.Text))
                {
                    if (!ReferenceEquals(claim, field) || !name!.StartsWith("FFmpeg", StringComparison.Ordinal))
                    {
                        evidence.Add(Evidence(claim.Source, $"Vorbis comment ENCODER field \"{claim.Text}\", which ffmpeg writes when it encodes"));
                    }

                    continue;
                }

                string text = claim.Text;
                _findings.Add(Severity.Info, "opus.encoder-other-format", claim.Source, _ => $"The Vorbis comment ENCODER field names \"{text}\", an encoder of another codec, not of Opus.");
                if (name is not null)
                {
                    evidence.Add(Evidence(claim.Source, $"Vorbis comment ENCODER field \"{text}\", an encoder of another codec, which does not count"));
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

        // The writer, from the comment's ENCODER field (RFC 7845 section 5.2: applications use it to identify themselves) and a vendor
        // string that is a muxer's version.
        private Node? AddWriter()
        {
            List<EncoderClaim> claims = CommentClaims();
            if (_comment is not { } tag || !ClaimRoles.Writers(claims).Any())
            {
                return null;
            }

            Node? writer = WriterDetection.Detect(claims, (name, value, description) => new Node(name, tag.Offset, tag.Length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
                (claim, text, name) => _findings.Add(Severity.Info, "opus.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
                text => OpusencEncoder().Match(text) is { Success: true } opusenc ? $"opusenc {opusenc.Groups[1].Value}" : ClaimRoles.WriterName(text),
                isEvidenceAtSource: true,
                format: new WriterDetection.StreamFormat(KnownWriters.Opus, "Opus", (claim, message, node) => _findings.Add(Severity.Info, "opus.writer-other-format", node, _ => message)));
            return writer is null ? null : _parent.Add(writer);
        }

        private static Node Evidence(Node at, string text) => new("Evidence", at.Offset, at.Length, text) { Description = Explain.Evidence };

        // Figures over the audio packets: their durations and the granule positions, the bitrate
        // from the packets' bytes, the packet sizes, and the frames by mode, bandwidth and size.
        // Returns the node the per-second series is shown after.
        private Node? AddStatistics(long samples, long trim)
        {
            if (_packets == 0)
            {
                return null;
            }

            long start = _audioStart;
            long length = _audioEnd - _audioStart;
            Node node = _parent.Add(new Node("Statistics", start, length, $"{Count(_packets, "packet", "packets")}, {(samples / (double)Rate).ToString("F3", CultureInfo.InvariantCulture)} s")
            {
                Kind = NodeKind.Statistics,
                Description = Explain.Statistics,
                Topic = Topics.OpusModes,
            });

            Node Add(Node parent, string name, NodeValue? value, string description, string? topic = null) => parent.Add(name, start, length, value, description, topic);

            Add(node, "Packets", NodeValue.Integer(_wholePackets), Explain.Packets);
            Add(node, "Frames", NodeValue.Integer(_frames), Explain.Frames);
            Add(node, "Coded Samples", NodeValue.Integer(_decoded, "samples", "sample"), Explain.CodedSamples, Topics.OpusToc);
            if (HasHead)
            {
                Add(node, "Start Trimming", NodeValue.Integer(_head!.PreSkip, "samples", "sample"), Explain.StatPreSkip, Topics.OpusPreSkip);
            }

            if (_firstOffset is { } offset)
            {
                Add(node, "Start Offset", NodeValue.Integer(offset, "samples", "sample"), Explain.StartOffset, Topics.GranulePosition);
                Add(node, "End Trimming", NodeValue.Integer(trim, "samples", "sample"), Explain.EndTrimming, Topics.GranulePosition);
            }

            if (_firstOffset is null && _isContained && context.Configuration?.DiscardPadding is not null)
            {
                Add(node, "End Trimming", NodeValue.Integer(trim, "samples", "sample"), Explain.EndTrimmingContainer, Topics.OpusPreSkip);
            }

            Add(node, "Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.Samples, Topics.OpusPreSkip);
            if (_lost > 0)
            {
                Add(node, "Lost Samples", NodeValue.Integer(_lost, "samples", "sample"), StreamInfo.LostSamplesDescription, StreamInfo.LostSamplesTopic);
            }

            Add(node, "Duration", NodeValue.Decimal(samples / (double)Rate, 3, "s"), Explain.Duration);
            if (_wholeDecoded > 0)
            {
                Add(node, "Average Bitrate", NodeValue.Decimal(_bytes * 8.0 * Rate / _wholeDecoded / 1000, 1, "kbps"), Explain.AverageBitrate);
            }

            var distribution = new BitrateDistribution();
            (double minimum, double maximum) = BitrateRange(distribution);
            if (maximum > 0)
            {
                Add(node, "Minimum Bitrate", NodeValue.Decimal(minimum, 1, "kbps"), Explain.MinimumBitrate);
                Add(node, "Maximum Bitrate", NodeValue.Decimal(maximum, 1, "kbps"), Explain.MaximumBitrate);
                _isVariable = distribution.IsVariable();
                distribution.AddTo(node, (name, value, description) => new Node(name, start, length, value) { Description = description }, "Packet", "Packets");
            }

            Node sizes = Add(node, "Packet Sizes", _wholePackets == 0 ? "none whole" : _sizesDiffer
                ? $"{_smallest} to {_largest} bytes, {Math.Round(_bytes / (double)_wholePackets, 1, MidpointRounding.ToEven).ToString("F1", CultureInfo.InvariantCulture)} on average"
                : $"all {Count(_smallest, "byte", "bytes")}, as CBR encoding writes them", _sizesDiffer ? Explain.PacketSizes : Explain.PacketSizesConstant, Topics.BitrateModes);

            if (_frames > 0)
            {
                Node modes = Add(node, "Modes", Count(_modes.Count(c => c > 0), "mode", "modes"), Explain.Modes, Topics.OpusModes);
                for (int m = 0; m < _modes.Length; m++)
                {
                    if (_modes[m] > 0)
                    {
                        Add(modes, Toc.ModeNames[m], Share(_modes[m], _frames, "frame", "frames"), Explain.ModeShare);
                    }
                }

                Node bandwidths = Add(node, "Bandwidths", Count(_bandwidths.Count(c => c > 0), "bandwidth", "bandwidths"), Explain.Bandwidths, Topics.OpusModes);
                for (int b = 0; b < _bandwidths.Length; b++)
                {
                    if (_bandwidths[b] > 0)
                    {
                        Add(bandwidths, $"{Toc.BandwidthNames[b]} ({Toc.BandwidthRanges[b]})", Share(_bandwidths[b], _frames, "frame", "frames"), Explain.BandwidthShare);
                    }
                }

                Node coded = Add(node, "Coded Bandwidth", null, Explain.StatCodedBandwidth, Topics.CodedBandwidth);
                int firstBand = Array.FindIndex(_bandwidths, c => c > 0);
                int mostBand = 0;
                for (int b = 1; b < _bandwidths.Length; b++)
                {
                    mostBand = _bandwidths[b] > _bandwidths[mostBand] ? b : mostBand;
                }

                Add(coded, "Lowest", NodeValue.Integer(Toc.BandwidthHertz[firstBand], "Hz"), Explain.BandwidthLowest);
                Add(coded, "Most Common", NodeValue.Integer(Toc.BandwidthHertz[mostBand], "Hz"), Explain.BandwidthMostCommon);
                Add(coded, "Highest", NodeValue.Integer(Toc.BandwidthHertz[Array.FindLastIndex(_bandwidths, c => c > 0)], "Hz"), Explain.BandwidthHighest);
                _codedBandwidthNode = coded;

                Node frameSizes = Add(node, "Frame Sizes", Count(_frameSizes.Count, "size", "sizes"), Explain.FrameSizes, Topics.OpusToc);
                foreach ((int size, long count) in _frameSizes)
                {
                    Add(frameSizes, Toc.FrameDuration(size), Share(count, _frames, "frame", "frames"), Explain.FrameSizeShare);
                }
            }

            if (_parts > 0)
            {
                string unit = _records.StreamCount > 1 ? "stream packet" : "packet";
                string units = unit + "s";
                Node channels = Add(node, "Channel Coding", NodeValue.Integer(_stereo[1], $"stereo {units}", $"stereo {unit}"), Explain.StereoCoding, Topics.OpusToc);
                Add(channels, "Stereo", Share(_stereo[1], _parts, unit, units), Explain.StereoShare);
                Add(channels, "Mono", Share(_stereo[0], _parts, unit, units), Explain.MonoShare);
                Node codes = Add(node, "Frame Packing", Count(_codes.Count(c => c > 0), "code", "codes"), Explain.FramePacking, Topics.OpusToc);
                string[] names = ["Code 0 (one frame)", "Code 1 (two frames of equal size)", "Code 2 (two frames of different sizes)", "Code 3 (any number of frames)"];
                for (int c = 0; c < 4; c++)
                {
                    if (_codes[c] > 0)
                    {
                        Add(codes, names[c], Share(_codes[c], _parts, unit, units), Explain.CodeShare);
                    }
                }
            }

            if (_celtFrames > 0)
            {
                Node blocks = Add(node, "Block Usage", NodeValue.Integer(_celtFrames, "CELT frames", "CELT frame"), Explain.CeltBlocks, Topics.BlockSwitching);
                Add(blocks, "Long Blocks", Share(_celtFrames - _transientFrames, _celtFrames, "frame", "frames"), Explain.UsageLong);
                Add(blocks, "Short Blocks", Share(_transientFrames, _celtFrames, "frame", "frames"), Explain.UsageShort);
                _blockUsageNode = blocks;
                if (_stereoCelt > 0)
                {
                    Node stereo = Add(node, "Stereo Coding", NodeValue.Integer(_stereoCelt, "stereo CELT frames", "stereo CELT frame"), Explain.CeltStereo, Topics.OpusCelt);
                    Add(stereo, "Mid/Side Stereo", Share(_angleFrames, _stereoCelt, "frame", "frames"), Explain.StereoAngle);
                    Add(stereo, "Dual Stereo", Share(_dualFrames, _stereoCelt, "frame", "frames"), Explain.StereoDual);
                    Add(stereo, "Intensity Stereo", Share(_intensityFrames, _stereoCelt, "frame", "frames"), Explain.StereoIntensity);
                    _stereoCodingNode = stereo;
                }

                if (_boundCount > 0)
                {
                    Node bound = Add(node, "Intensity Stereo Bound", null, Explain.IntensityBound, Topics.IntensityStereo);
                    Add(bound, "Lowest", NodeValue.Integer(_lowestBound, "Hz"), Explain.BoundLowest);
                    Add(bound, "Highest", NodeValue.Integer(_highestBound, "Hz"), Explain.BoundHighest);
                    _boundNode = bound;
                }
            }

            if (_redundantFrames > 0)
            {
                Add(node, "Frames With Redundancy", NodeValue.Integer(_redundantFrames), Explain.RedundantFrames, Topics.OpusModes);
            }

            if (_emptyFrames > 0)
            {
                Add(node, "Frames Without Data", NodeValue.Integer(_emptyFrames), Explain.EmptyFrames);
            }

            if (_padding > 0)
            {
                Add(node, "Padding", NodeValue.Integer(_padding, "bytes", "byte"), Explain.PaddingTotal);
            }

            return sizes;
        }

        // A packet's bitrate: its bits over the samples it codes.
        private (double Minimum, double Maximum) BitrateRange(BitrateDistribution distribution)
        {
            double minimum = double.MaxValue;
            double maximum = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                if (i % 65536 == 0)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }

                int samples = _records.SamplesOf(i);
                if (samples == 0 || _cutPackets.Contains(i))
                {
                    continue;
                }

                double bitrate = _records.LengthOf(i) * 8.0 * Rate / samples / 1000;
                minimum = Math.Min(minimum, bitrate);
                maximum = Math.Max(maximum, bitrate);
                distribution.Add(bitrate);
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
            return _parent.Add(new Node("Audio Packets", _audioStart, _audioEnd - _audioStart, NodeValue.Integer(_packets, "packets", "packet"), () => new BlockedChildren([], records.Count, i => PacketNode(records, i)))
            {
                Kind = NodeKind.Stream,
                Description = Explain.AudioPackets,
                Topic = Topics.OpusToc,
            });
        }

        // Per packet, its bitrate at its offset (0 for one that codes no samples); per second of
        // audio, the bits of the packets that start in it over their duration, rounded half to even
        // to 0.1 kbps.
        private Node? _codedBandwidthNode;
        private Node? _blockUsageNode;
        private Node? _stereoCodingNode;
        private Node? _boundNode;

        private (List<Series> Series, IReadOnlyList<CategorySeries> Categories) Series(Node? anchor, Node? packets)
        {
            if (_packets == 0 || packets is null || _decoded == 0)
            {
                return ([], []);
            }

            int count = _records.Count;
            double[] offsets = _records.OffsetsForSeries(context.CancellationToken);
            var bitrates = new double[count];
            var seconds = new List<(long Bits, long Samples)>();
            var highest = new List<int>();
            var bandwidthX = new double[count];
            var bandwidthY = new double[count];
            int bandwidthCount = 0;
            long position = 0;
            for (int i = 0; i < count; i++)
            {
                if (i % 4096 == 0)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }

                int samples = _records.SamplesOf(i);
                if (samples == 0 || _cutPackets.Contains(i))
                {
                    continue;
                }

                int bytes = _records.LengthOf(i);
                bitrates[i] = bytes * 8.0 * Rate / samples / 1000;
                int second = (int)(position / Rate);
                while (seconds.Count <= second)
                {
                    seconds.Add(default);
                    highest.Add(-1);
                }

                int band = -1;
                for (int s = 0; s < _records.PartCount(i); s++)
                {
                    PartRecord part = _records.Part(i, s);
                    if (part.HasToc && part.Status == PartStatus.Valid)
                    {
                        band = Math.Max(band, Toc.Bandwidth(part.TocByte));
                    }
                }

                if (band >= 0)
                {
                    bandwidthX[bandwidthCount] = offsets[i];
                    bandwidthY[bandwidthCount++] = Toc.BandwidthHertz[band];
                    highest[second] = Math.Max(highest[second], Toc.BandwidthHertz[band]);
                }

                seconds[second] = (seconds[second].Bits + (bytes * 8L), seconds[second].Samples + samples);
                position += samples;
            }

            double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
            double[] rates = seconds
                .Select(s => s.Samples > 0 ? Math.Round(s.Bits * (double)Rate / s.Samples / 1000, 1, MidpointRounding.ToEven) : 0)
                .ToArray();
            var bitrate = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = SeriesText.Bitrate };
            var series = new List<Series>
            {
                bitrate,
                new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, anchor) { Description = SeriesText.BitrateOverTime },
            };
            var categories = new List<CategorySeries>();
            if (_celtFrames > 0 && _blockUsageNode is not null)
            {
                categories.Add(_blockUsage.Build(offsets, bitrate, _blockUsageNode, SeriesText.BlockUsage, Topics.BlockSwitching));
                if (_stereoCelt > 0 && _stereoCodingNode is not null)
                {
                    categories.Add(_stereoCoding.Build(offsets, bitrate, _stereoCodingNode, SeriesText.StereoCoding, Topics.OpusCelt));
                }
            }

            if (_boundCount > 0 && _boundNode is not null)
            {
                var boundX = new double[_boundCount];
                var boundY = new double[_boundCount];
                for (int i = 0, n = 0; i < count; i++)
                {
                    if (_bounds[i] != NoBound)
                    {
                        boundX[n] = offsets[i];
                        boundY[n++] = _bounds[i];
                    }
                }

                series.Add(new Series("Intensity Stereo Bound", "Hz", SeriesAxis.FrameOffset, boundX, boundY)
                {
                    Description = SeriesText.IntensityStereoBound,
                    Concept = GraphConcept.StereoCoding,
                    Quantity = "Coded Bandwidth",
                });
            }

            if (bandwidthCount > 0)
            {
                Array.Resize(ref bandwidthX, bandwidthCount);
                Array.Resize(ref bandwidthY, bandwidthCount);
                series.Add(new("Coded Bandwidth", "Hz", SeriesAxis.FrameOffset, bandwidthX, bandwidthY)
                {
                    Description = SeriesText.CodedBandwidth,
                    Concept = GraphConcept.CodedBandwidth,
                    Quantity = "Coded Bandwidth",
                });
                int valued = 0;
                for (int s = 0; s < highest.Count; s++)
                {
                    valued += highest[s] >= 0 ? 1 : 0;
                }

                var bandStarts = new double[valued];
                var bandHighest = new double[valued];
                for (int s = 0, n = 0; s < highest.Count; s++)
                {
                    if (highest[s] >= 0)
                    {
                        bandStarts[n] = s;
                        bandHighest[n++] = highest[s];
                    }
                }

                series.Add(new("Coded Bandwidth Over Time", "Hz", SeriesAxis.Seconds, bandStarts, bandHighest, _codedBandwidthNode)
                {
                    Description = SeriesText.CodedBandwidthOverTime,
                    Concept = GraphConcept.CodedBandwidth,
                    Quantity = "Coded Bandwidth",
                });
            }

            return (series, categories);
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

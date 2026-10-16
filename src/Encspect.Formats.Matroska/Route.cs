using System.Globalization;
using System.IO.Compression;
using Encspect.Core;

namespace Encspect.Formats.Matroska;

internal sealed partial class FileReader
{
    // Frames larger than this are not decompressed.
    private const int MaxInflated = 1 << 26;

    private void RouteTracks(SegmentState segment)
    {
        PrepareWriterClaims(segment);
        foreach (TrackInfo track in segment.Tracks)
        {
            string label = track.Number is { } n ? $"Track {n}" : "A track without a number";
            if (!track.IsAudio)
            {
                string kind = Values.TrackTypeName(track.Type ?? 0) ?? "of an unknown type";
                _findings.AddNow(Severity.Info, "matroska.track-not-analyzed",
                    $"{label} is {(track.Type is null ? "of no stated type" : kind == "of an unknown type" ? kind : $"a {kind} track")}{(track.CodecId is null ? string.Empty : $" ({track.CodecId})")}, which is read at container level only: its blocks are reported as they are.", track.Node);
                continue;
            }

            // The audio tracks of a Segment play together, so a summary of the file does not add their durations up. A track whose codec
            // reported several streams is not marked: those are not known to play together.
            int streamsBefore = _context.Streams.Count;
            RouteAudio(segment, track, label);
            List<StreamInfo> made = [.. _context.Streams.Skip(streamsBefore)];
            if (made.Count == 1)
            {
                StreamInfo stream = made[0];
                // A codec parser that finds no tag to put a Writer in (an Opus track has no comment packet) names no writer: the claims of the
                // Segment and the file's tags are weighed here.
                if (stream.Writer is null && stream.Nodes.Count > 0 && Codecs.WriterKind(track.CodecId ?? string.Empty) is { } kind)
                {
                    Node? writer = ResolveWriter(segment, stream.Nodes[0], kind, Codecs.NameOf(track.CodecId ?? string.Empty) ?? kind);
                    (string? writerName, Confidence? writerConfidence) = ClaimRoles.Resolved(writer);
                    stream = stream with { Writer = writerName, WriterConfidence = writerConfidence };
                }

                _context.UpdateStream(made[0], stream with { PlaybackGroup = segment.Number });
            }
        }
    }

    // The Segment's WritingApp and MuxingApp (RFC 9559, sections 5.1.2.14 and 5.1.2.13: "Writing application", "Muxing application or library") are
    // claims of the writer role (a "Lavc" encoder ID takes the encoder role, ClaimRoles.Of), WritingApp first so that on a tie the application
    // that wrote this file outweighs the library that muxed it. They belong to the Segment (each has its own Info) and reach the codec parsers of
    // its tracks through the route's claims, never through the file's, so no other Segment sees them.
    // - A MuxingApp that names the Matroska libraries (mkvtoolnix writes "libebml v1.4.5 + libmatroska v1.7.1") is the library under the
    //   WritingApp that uses it and never conflicts with it: the codec parsers do not get it, and the container's own Writer node holds it as evidence.
    // - A MuxingApp and a WritingApp that name different software are one finding per Segment (not one per track); the codec parsers then
    //   get the WritingApp alone.
    private void PrepareWriterClaims(SegmentState segment)
    {
        EncoderClaim? writing = segment.WritingApp is { } w && segment.WritingAppNode is { } wn && !string.IsNullOrWhiteSpace(w)
            ? new EncoderClaim(w, wn, "Segment WritingApp element", ClaimRoles.Of(ClaimRole.Writer, w)) : null;
        EncoderClaim? muxing = segment.MuxingApp is { } m && segment.MuxingAppNode is { } mn && !string.IsNullOrWhiteSpace(m)
            ? new EncoderClaim(m, mn, "Segment MuxingApp element", ClaimRoles.Of(ClaimRole.Writer, m)) : null;
        segment.OwnClaims.AddRange(new[] { writing, muxing }.OfType<EncoderClaim>());
        if (writing is null || muxing is null)
        {
            segment.StreamClaims.AddRange(segment.OwnClaims);
            return;
        }

        bool isLibrary = IsMatroskaLibrary(muxing.Text);
        bool differs = writing.Role == ClaimRole.Writer && muxing.Role == ClaimRole.Writer && !isLibrary && ClaimRoles.FamilyOf(writing.Text) != ClaimRoles.FamilyOf(muxing.Text);
        segment.StreamClaims.Add(writing);
        if (differs)
        {
            _findings.AddNow(Severity.Info, "matroska.writer-claim-differs",
                WriterDetection.Disagreement(muxing, muxing.Text.Trim(), $"the {writing.Description} names \"{ClaimRoles.WriterName(writing.Text)}\""), muxing.Source);
        }
        else if (!isLibrary)
        {
            segment.StreamClaims.Add(muxing);
        }
    }

    private static bool IsMatroskaLibrary(string text) => text.StartsWith("libebml", StringComparison.Ordinal) || text.StartsWith("libmatroska", StringComparison.Ordinal);

    // The family of a library MuxingApp is its WritingApp's: the application uses the library.
    private static string FamilyIn(SegmentState segment, string text) =>
        IsMatroskaLibrary(text) && segment.WritingApp is { } writing && !string.IsNullOrWhiteSpace(writing) ? ClaimRoles.FamilyOf(writing) : ClaimRoles.FamilyOf(text);

    private void RouteAudio(SegmentState segment, TrackInfo track, string label)
    {
        TrackStatistics statistics = track.Statistics;
        string codecId = track.CodecId ?? string.Empty;
        string? codec = Codecs.RouteOf(codecId);
        Node anchor = track.CodecIdNode ?? track.Node;
        if (statistics.Frames == 0)
        {
            _findings.AddNow(Severity.Info, "matroska.track-empty", $"{label} ({(codecId.Length == 0 ? "no codec ID" : codecId)}) has no block with frames, so there is nothing to analyze.", track.Node);
            return;
        }

        (Decoder? frames, Decoder? priv, string? problem, bool isEncrypted) = Decoders(track);
        if (problem is null && (frames is not null || priv is not null) && codec is not null and not ("vorbis" or "opus"))
        {
            // The AAC, AC-3, DTS and MPEG audio parsers place their nodes at the offsets of contiguous file bytes, which frames put together from
            // stripped or inflated bytes do not have.
            problem = $"stores its frames with {(track.Encodings.Any(e => e.CompressionAlgorithm == 0) ? "zlib compression" : "header stripping")}, which the {Codecs.NameOf(codecId)} parser cannot map back to the bytes of the file";
        }

        if (problem is not null)
        {
            _findings.AddNow(Severity.Info, isEncrypted ? "matroska.track-encrypted" : "matroska.track-encoding-not-read",
                $"{label} ({codecId}) {problem}, so its frames cannot be analyzed.", track.Encodings.FirstOrDefault()?.Node ?? track.Node);
            AddContainerStream(segment, track, null);
            return;
        }

        if (codec is null)
        {
            _findings.AddNow(Severity.Info, "matroska.codec-not-analyzed",
                Codecs.NameOf(codecId) is { } name
                    ? $"{label} is coded as {name} ({codecId}), whose frames are not analyzed yet; the track is reported from its TrackEntry."
                    : $"{label} has the codec ID \"{codecId}\", which the Matroska codec mapping does not define for audio, so its frames are not analyzed.",
                anchor);
            AddContainerStream(segment, track, null);
            return;
        }

        MappedBytes? privateData = null;
        if (track.CodecPrivateOffset >= 0 && track.CodecPrivateLength > 0 && track.CodecPrivateLength <= MaxInflated)
        {
            try
            {
                privateData = priv is null
                    ? new MappedBytes(ReadAt(track.CodecPrivateOffset, (int)track.CodecPrivateLength), track.CodecPrivateOffset)
                    : priv.Decode(ReadAt(track.CodecPrivateOffset, (int)track.CodecPrivateLength), track.CodecPrivateOffset, this);
            }
            catch (InvalidDataException ex)
            {
                _findings.AddNow(Severity.Warning, "matroska.codec-private-invalid",
                    $"The CodecPrivate of {label} cannot be read back from its ContentEncodings (RFC 9559, section 5.1.4.1.31): {ex.Message} Its frames cannot be analyzed.", track.CodecPrivateNode ?? anchor);
                AddContainerStream(segment, track, null);
                return;
            }
        }

        // The legacy AAC IDs carry no CodecPrivate ("Initialization: none", draft-ietf-cellar-codec, section 3.4.2): the AudioSpecificConfig is
        // worked out from the ID and the Audio element following ISO/IEC 14496-3 (FFmpeg's matroskadec.c does the same for the common rates), and mapped to the CodecID that gives its values.
        Node? configurationNode = track.CodecPrivateNode;
        if (privateData is null && codec == "aac" && track.CodecIdNode is { } idNode
            && Codecs.LegacyAacConfig(codecId, track.SamplingFrequency, track.OutputSamplingFrequency, track.Channels) is { } synthesized)
        {
            privateData = MappedBytes.Opaque(synthesized, idNode.Offset, idNode.Offset + idNode.Length);
            configurationNode = idNode;
        }

        List<MappedBytes> headers = [];
        if (codec is "vorbis" or "opus")
        {
            if (privateData is null)
            {
                _findings.AddNow(Severity.Warning, "matroska.codec-private-missing",
                    $"{label} is coded as {Codecs.NameOf(codecId)} but has no CodecPrivate, which holds its {(codec == "vorbis" ? "three headers" : "identification header")} (draft-ietf-cellar-codec, section 3.4); its frames cannot be analyzed.", anchor);
                AddContainerStream(segment, track, null);
                return;
            }

            if (codec == "opus")
            {
                headers.Add(privateData);
            }
            else if (!TryUnlaceVorbisHeaders(privateData, headers, out string? why))
            {
                _findings.AddNow(Severity.Warning, "matroska.codec-private-invalid",
                    $"The CodecPrivate of {label} does not hold the three Xiph-laced Vorbis headers the codec mapping gives it (draft-ietf-cellar-codec, section 3.4): it {why}. Its frames cannot be analyzed.", track.CodecPrivateNode ?? anchor);
                AddContainerStream(segment, track, null);
                return;
            }
        }

        string prefix = _segments.Count > 1 ? $"Segment {segment.Number} " : string.Empty;

        // The headers handed over as packets (Vorbis, Opus) lie in the CodecPrivate, which comes before the first frame in a well-ordered
        // file and may come after the last in a damaged one.
        long first = statistics.FirstOffset;
        long last = statistics.LastEnd;
        foreach (MappedBytes header in headers)
        {
            first = Math.Min(first, header.FileStart);
            last = Math.Max(last, header.FileEnd);
        }

        var node = new Node($"{prefix}Track {track.Number} Frames", first, Math.Max(0, last - first), NodeValue.Integer(statistics.Frames, "frames", "frame"))
        {
            Description = Explain.TrackFrames,
            Topic = Topics.Tracks,
        };
        _file.Add(node);

        CodecConfiguration? configuration = privateData is not null && configurationNode is { } privateNode
            ? new CodecConfiguration(privateData, privateNode) { Delay = track.CodecDelayNode is null ? null : track.CodecDelay / 1e9 }
            : null;
        int streamsBefore = _context.Streams.Count;
        bool routed = _context.ParsePackets(codec, node, Packets(segment, track, headers, frames), configuration, segment.StreamClaims);
        if (!routed)
        {
            _findings.AddNow(Severity.Info, "matroska.codec-not-analyzed",
                $"{label} is coded as {Codecs.NameOf(codecId)} ({codecId}), but the analyzer has no parser for the {codec} coding.", anchor);
        }

        List<StreamInfo> added = [.. _context.Streams.Skip(streamsBefore)];
        if (added.Count == 0)
        {
            AddContainerStream(segment, track, node);
        }
        else
        {
            CompareStreams(track, label, codec, added);
        }
    }

    // The three headers of A_VORBIS (draft-ietf-cellar-codec, section 3.4.41): the number of packets minus one, the Xiph-laced lengths of
    // all but the last, then the packets.
    private static bool TryUnlaceVorbisHeaders(MappedBytes data, List<MappedBytes> headers, out string? why)
    {
        why = null;
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < 1 || span[0] != 2)
        {
            why = span.Length < 1 ? "is empty" : $"counts {span[0] + 1} packets";
            return false;
        }

        int position = 1;
        var sizes = new int[2];
        for (int i = 0; i < 2; i++)
        {
            long size = 0;
            while (true)
            {
                if (position >= span.Length)
                {
                    why = "ends inside the packet lengths";
                    return false;
                }

                byte value = span[position++];
                size += value;
                if (value != 255)
                {
                    break;
                }
            }

            if (size > span.Length)
            {
                why = "names packets larger than itself";
                return false;
            }

            sizes[i] = (int)size;
        }

        if (position + sizes[0] + sizes[1] > span.Length)
        {
            why = "names packets larger than itself";
            return false;
        }

        headers.Add(data.Slice(position, sizes[0]));
        headers.Add(data.Slice(position + sizes[0], sizes[1]));
        headers.Add(data.Slice(position + sizes[0] + sizes[1]));
        return true;
    }

    // The frames of a track in file order, each as the bytes it came from (with the track's content encoding undone), after the headers
    // that come from the CodecPrivate.
    private IEnumerable<MappedBytes> Packets(SegmentState segment, TrackInfo track, List<MappedBytes> headers, Decoder? decoder)
    {
        foreach (MappedBytes header in headers)
        {
            yield return header;
        }

        Records records = segment.Records;
        ulong number = track.Number ?? 0;
        for (int i = 0; i < records.Blocks.Count; i++)
        {
            if (i % 4096 == 0)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
            }

            BlockRecord block = records.Blocks[i];
            if (block.Track != number || block.Status is not (BlockStatus.None or BlockStatus.LaceSingle))
            {
                continue;
            }

            long position = block.FramesOffset;
            for (int f = 0; f < block.Frames; f++)
            {
                int size = (int)records.FrameSize(block, f);
                byte[] data = ReadAt(position, size);
                MappedBytes packet;
                try
                {
                    packet = decoder is null ? new MappedBytes(data, position) : decoder.Decode(data, position, this);
                }
                catch (InvalidDataException ex)
                {
                    _findings.AddNow(Severity.Warning, "matroska.frame-encoding-invalid",
                        $"A frame of Track {number} at 0x{position:X} cannot be read back from the track's ContentEncodings (RFC 9559, section 5.1.4.1.31): {ex.Message} The frames from there on are not analyzed.", track.Node);
                    yield break;
                }

                yield return packet;
                position += size;
            }
        }
    }

    // What the container knows of a stream the codec parser did not report: the TrackEntry's audio settings and the totals of the blocks.
    private void AddContainerStream(SegmentState segment, TrackInfo track, Node? frames)
    {
        TrackStatistics statistics = track.Statistics;
        string codecId = track.CodecId ?? string.Empty;
        double scale = segment.TimestampScale / 1e9;
        double span = statistics.Blocks > 0 && statistics.HasTimes ? (statistics.Largest - statistics.Smallest) * scale * track.TimestampScale : 0;
        double step = statistics.Blocks > 1 ? span / (statistics.Blocks - 1) : (track.DefaultDuration is { } d ? d / 1e9 * Math.Max(1, statistics.Frames) : 0);
        double duration = statistics.HasTimes ? span + step : 0;
        double rate = track.OutputSamplingFrequency ?? track.SamplingFrequency ?? (track.HasAudio ? 8000 : 0);
        int channels = track.HasAudio ? (int)Math.Min(track.Channels ?? 1, int.MaxValue) : 0;
        Node streamNode = frames ?? track.Node;
        Node? writer = ResolveWriter(segment, streamNode, Codecs.RouteOf(codecId) is { } route ? Codecs.WriterKind(codecId) ?? route : codecId, Codecs.NameOf(codecId) ?? codecId);
        (string? writerName, Confidence? writerConfidence) = ClaimRoles.Resolved(writer);
        _context.AddStream(new StreamInfo
        {
            Nodes = [streamNode],
            Codec = Codecs.NameOf(codecId) ?? (codecId.Length == 0 ? "unknown" : codecId),
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = channels,
            BitsPerSample = track.BitDepth is { } depth && codecId.StartsWith("A_PCM", StringComparison.Ordinal) ? (int)depth : null,
            Duration = duration,
            AverageBitrate = duration > 0 ? statistics.Bytes * 8 / duration / 1000 : null,
            Writer = writerName,
            WriterConfidence = writerConfidence,
        });
    }

    // The writer of a stream the codec parser names none for, from the claims of the file (its tags) and of the Segment, the tags' first as the
    // router hands them to the codec parsers. Software known to write another kind of stream is a finding, as in the codec parsers. The
    // Writer node is made once for the Segment, in its Info element, which holds the claims' elements: the evidence points at them; a
    // Segment without an Info has the node under the stream's. A finding is reported once for the Segment, however many streams it holds.
    private Node? ResolveWriter(SegmentState segment, Node stream, string kind, string label)
    {
        // The Segment's own conflict is reported once, in PrepareWriterClaims; the library MuxingApp joins as evidence.
        List<EncoderClaim> claims = [.. _context.EncoderClaims, .. segment.OwnClaims.Where(c => segment.StreamClaims.Contains(c) || IsMatroskaLibrary(c.Text))];
        Node place = segment.InfoNode ?? stream;
        void Report(string code, string text, Node node)
        {
            if (segment.Reported.Add($"{code}|{node.Offset}|{text}"))
            {
                _findings.AddNow(Severity.Info, code, text, node);
            }
        }

        Node? writer = WriterDetection.Detect(claims,
            (name, value, description) => new Node(name, place.Offset, place.Length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => Report("matroska.writer-claim-differs", WriterDetection.Disagreement(claim, text, name), claim.Source),
            isEvidenceAtSource: claims.All(c => c.Source.Offset >= place.Offset && c.Source.End <= place.End),
            familyOf: text => FamilyIn(segment, text),
            format: new WriterDetection.StreamFormat(kind, label, (claim, message, node) => Report("matroska.writer-other-format", message, node)));
        if (writer is null)
        {
            return null;
        }

        if (segment.InfoNode is null)
        {
            stream.Add(writer);
        }
        else if (segment.WriterNode is null)
        {
            segment.WriterNode = segment.InfoNode.Add(writer);
        }

        return writer;
    }

    // The codec's stream against the TrackEntry's Audio element: the sample rate and the number of channels must agree where the
    // codec states them (draft-ietf-cellar-codec, section 3.4).
    private void CompareStreams(TrackInfo track, string label, string codec, List<StreamInfo> streams)
    {
        foreach (StreamInfo stream in streams)
        {
            if (!track.HasAudio)
            {
                return;
            }

            bool rateKnown = track.SamplingFrequency is not null || track.OutputSamplingFrequency is not null;
            bool rateMatches = stream.SampleRate == 0 || codec == "opus" || !rateKnown
                || stream.SampleRate == (int)Math.Round(track.SamplingFrequency ?? 0) || stream.SampleRate == (int)Math.Round(track.OutputSamplingFrequency ?? 0);
            if (!rateMatches)
            {
                string stated = track.SamplingFrequency is { } rate ? rate.ToString("0.###", CultureInfo.InvariantCulture) : "none";
                _findings.AddNow(Severity.Warning, "matroska.sample-rate-differs",
                    $"{label} states a sampling frequency of {stated} Hz{(track.OutputSamplingFrequency is { } output ? $" and an output sampling frequency of {output.ToString("0.###", CultureInfo.InvariantCulture)} Hz" : string.Empty)}, but its {stream.Codec} frames are coded at {stream.SampleRate} Hz.",
                    track.SamplingFrequencyNode ?? track.AudioNode ?? track.Node);
            }

            ulong stated2 = track.Channels ?? 1;
            bool parametric = codec == "aac" && (stated2 == 1 || stream.Channels == 1) && (stated2 == 2 || stream.Channels == 2);
            if (stream.Channels != 0 && (ulong)stream.Channels != stated2 && !parametric)
            {
                _findings.AddNow(Severity.Warning, "matroska.channels-differ",
                    $"{label} states {stated2} {(stated2 == 1 ? "channel" : "channels")}, but its {stream.Codec} frames hold {stream.Channels}.", track.ChannelsNode ?? track.AudioNode ?? track.Node);
            }
        }
    }

    // The track's ContentEncodings undone for its frames and its CodecPrivate (RFC 9559 section 5.1.4.1.31): the encodings that apply,
    // from the highest ContentEncodingOrder down. Header stripping and zlib are undone; encryption and the other compressions are
    // not, and the problem says so.
    private (Decoder? Frames, Decoder? Private, string? Problem, bool IsEncrypted) Decoders(TrackInfo track)
    {
        if (track.Encodings.Count == 0)
        {
            return (null, null, null, false);
        }

        List<EncodingInfo> ordered = [.. track.Encodings.OrderByDescending(e => e.Order)];
        var frames = new List<Step>();
        var priv = new List<Step>();
        foreach (EncodingInfo encoding in ordered)
        {
            if (encoding.HasEncryption || encoding.Type == 1)
            {
                string algorithm = Values.Named("ContentEncAlgo", encoding.EncryptionAlgorithm) ?? $"algorithm {encoding.EncryptionAlgorithm}";
                return (null, null, $"is encrypted ({algorithm}{(encoding.CipherMode is { } mode ? $", {Values.Named("AESSettingsCipherMode", mode) ?? $"mode {mode}"}" : string.Empty)})", true);
            }

            Step? step = encoding.CompressionAlgorithm switch
            {
                3 => encoding.SettingsLength is >= 0 and <= 1 << 16 ? new Step(true, encoding.SettingsOffset >= 0 ? ReadAt(encoding.SettingsOffset, (int)encoding.SettingsLength) : []) : null,
                0 => new Step(false, []),
                _ => null,
            };
            if (step is null)
            {
                string algorithm = Values.Named("ContentCompAlgo", encoding.CompressionAlgorithm) ?? $"algorithm {encoding.CompressionAlgorithm}";
                return (null, null, $"is compressed with {algorithm}, which the analyzer does not undo", false);
            }

            if (encoding.AppliesToFrames)
            {
                frames.Add(step);
            }

            if (encoding.AppliesToCodecPrivate)
            {
                priv.Add(step);
            }
        }

        return (frames.Count == 0 ? null : new Decoder(frames), priv.Count == 0 ? null : new Decoder(priv), null, false);
    }

    private sealed record Step(bool IsStripping, byte[] Prefix);

    // Undoes the steps in order: stripped bytes are put back in front of the data, zlib data is inflated.
    private sealed class Decoder(List<Step> steps)
    {
        public MappedBytes Decode(byte[] data, long offset, FileReader owner)
        {
            byte[] current = data;
            foreach (Step step in steps)
            {
                current = step.IsStripping ? [.. step.Prefix, .. current] : Inflate(current);
            }

            return MappedBytes.Opaque(current, offset, offset + data.Length);
        }

        private static byte[] Inflate(byte[] data)
        {
            bool isTooLarge = false;
            try
            {
                using var input = new MemoryStream(data);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > MaxInflated)
                    {
                        isTooLarge = true;
                        break;
                    }
                }

                if (!isTooLarge)
                {
                    return output.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new InvalidDataException("The zlib-compressed data is damaged or cut short (RFC 1950).");
            }

            throw new InvalidDataException("The zlib-compressed data inflates to more than the analyzer reads.");
        }
    }
}

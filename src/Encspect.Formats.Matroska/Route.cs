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
        int streamsBefore = _context.Streams.Count;
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

            RouteAudio(segment, track, label);
        }

        // WRITER CLAIMS (not implemented here): the Segment's MuxingApp and WritingApp (segment.MuxingApp, segment.WritingApp and their
        // nodes, collected in Collect.cs) name the program that wrote the container and the one that produced it ("Lavf60.16.100",
        // "libebml v1.4.5 + libmatroska v1.7.1" / "mkvmerge v81.0"). They are claims about the writer, not the encoder of the audio. When the
        // encoder/writer split lands (branch feature/encoder-writer: ClaimRole.Writer and StreamInfo.Writer), add one writer claim per
        // element here, after the streams are routed, with the element's node as evidence, and attach it to every audio stream of the Segment.
        // The audio encoder stays decided by the codec parsers.

        // The audio tracks of a Segment play together, so a summary of the file does not add their durations up.
        List<StreamInfo> added = [.. _context.Streams.Skip(streamsBefore)];
        if (added.Count > 1)
        {
            foreach (StreamInfo stream in added)
            {
                _context.UpdateStream(stream, stream with { IsParallel = true });
            }
        }
    }

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
        if (problem is null && (frames is not null || priv is not null) && codec is not null and not ("aac" or "vorbis" or "opus"))
        {
            // The AC-3, DTS and MPEG audio parsers place their nodes at the offsets of contiguous file bytes, which frames put together from
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
            privateData = priv is null
                ? new MappedBytes(ReadAt(track.CodecPrivateOffset, (int)track.CodecPrivateLength), track.CodecPrivateOffset)
                : priv.Decode(ReadAt(track.CodecPrivateOffset, (int)track.CodecPrivateLength), track.CodecPrivateOffset, this);
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

        // The headers handed over as packets (Vorbis, Opus) lie in the CodecPrivate, before the first frame in the Tracks element.
        long first = headers.Count > 0 ? Math.Min(headers[0].FileStart, statistics.FirstOffset) : statistics.FirstOffset;
        var node = new Node($"{prefix}Track {track.Number} Frames", first, Math.Max(0, statistics.LastEnd - first), NodeValue.Integer(statistics.Frames, "frames", "frame"))
        {
            Description = Explain.TrackFrames,
            Topic = Topics.Tracks,
        };
        _file.Add(node);

        CodecConfiguration? configuration = privateData is not null && track.CodecPrivateNode is { } privateNode
            ? new CodecConfiguration(privateData, privateNode) { Delay = track.CodecDelay / 1e9 }
            : null;
        int streamsBefore = _context.Streams.Count;
        bool routed = _context.ParsePackets(codec, node, Packets(segment, track, headers, frames), configuration);
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
                int size = block.Frames > 1 ? records.LaceSizes[block.Lace + f] : (int)(block.End - position);
                byte[] data = ReadAt(position, size);
                yield return decoder is null ? new MappedBytes(data, position) : decoder.Decode(data, position, this);
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
        _context.AddStream(new StreamInfo
        {
            Nodes = [frames ?? track.Node],
            Codec = Codecs.NameOf(codecId) ?? (codecId.Length == 0 ? "unknown" : codecId),
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = channels,
            BitsPerSample = track.BitDepth is { } depth && codecId.StartsWith("A_PCM", StringComparison.Ordinal) ? (int)depth : null,
            Duration = duration,
            AverageBitrate = duration > 0 ? statistics.Bytes * 8 / duration / 1000 : null,
        });
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
                    throw new InvalidDataException("A zlib-compressed frame inflates to more than the analyzer reads.");
                }
            }

            return output.ToArray();
        }
    }
}

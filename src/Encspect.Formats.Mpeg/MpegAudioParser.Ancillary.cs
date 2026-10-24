using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// mp3PRO, MP3 Surround and the Original File Length (OFL) block, which their encoders and
// Fraunhofer's mp3sEncoder write into the ancillary data of Layer III frames. No specification is
// public, so the rules are those of "Ancillary data" in docs/formats/mpeg-audio.md, which match
// mp3guessenc 0.27.6's output as the MPEG cross-check confirms: a frame's ancillary segment runs
// from the end of its main data, rounded up to a byte, to where the next frame's main data starts
// in the bit reservoir, and the last frame's to the end of its data. mp3PRO and MP3 Surround
// encoders start every segment with signature bits; an OFL block starts the first frame's segment.
// In mp3PRO, the OFL block counts samples at the rate an mp3PRO decoder outputs, twice the core's:
// Thomson's mp3PRO Encoder 1.1 stores 88200 samples for two seconds at 44100 Hz in a 22050 Hz
// stream (checked by hand; check_restricted_encoders.py).
public sealed partial class MpegAudioParser
{
    // A short stream would match mp3PRO's three signature bits by chance: 1 in 8 per frame.
    private const int MinEnhancedSegments = 8;

    // The signature, detection and OFL block belong to each stream of the region (the rules'
    // Terms); the counts the statistics show are the region's, summed over its streams.
    private static void ScanAncillary(StreamState state, RegionState region)
    {
        var scan = new AncillaryScan(state.Blocks);
        ChunkedList<Entry> frames = state.Build.Frames;
        int end = region.End ?? frames.Count;
        for (int i = region.First; i < end; i++)
        {
            state.CheckCancellation(i - region.First);
            ref readonly Entry entry = ref frames[i];
            if (!entry.IsGap)
            {
                scan.Add(entry);
            }
        }

        scan.Close();
        region.Ancillary = scan;
        foreach (StreamAncillary stream in scan.Streams)
        {
            if (stream.First is not null && stream.AudioFrames > 0)
            {
                Report(state, region, stream);
            }
        }
    }

    private static void Report(StreamState state, RegionState region, StreamAncillary stream)
    {
        MpegHeader header = stream.Header;
        long start = stream.AudioStart;
        long length = stream.AudioEnd - start;
        byte[] signature = stream.Signature;
        string? name = null;
        if (stream.Segments >= MinEnhancedSegments && stream.Bytes > 0)
        {
            if (!header.IsMpeg1 && (signature[0] & 0xC0) == 0xC0 && (signature[1] & 0x08) == 0x08)
            {
                name = "mp3PRO";
            }
            else if (header.IsMpeg1 && header.SampleRate is 44100 or 48000 && (signature[0] & 0xCF) == 0xCF && (signature[1] & 0x30) == 0x30)
            {
                name = "MP3 Surround";
            }
        }

        if (name is not null)
        {
            Node node = new("Enhancement", start, length, name) { Description = Explain.Enhancement, Topic = name == "mp3PRO" ? Topics.Mp3Pro : Topics.Mp3Surround };
            node.Add("Ancillary Data", start, length, Bytes(stream.Bytes), Explain.EnhancementBytes);
            node.Add("Data Rate", start, length, NodeValue.Decimal(RoundHalfEven((Int128)stream.Bytes * 8 * Clock * 10, (Int128)stream.Ticks * 1000) / 10.0, 1, "kbps"), Explain.EnhancementRate);
            if (region.Enhancement is null)
            {
                region.Enhancement = node;
            }
            else
            {
                region.MoreAncillary.Add(node);
            }

            string message = name == "mp3PRO"
                ? $"The stream is mp3PRO: {Counts.Of(stream.Bytes, "byte", "bytes")} of ancillary data carry its spectral band replication data. Decoders without mp3PRO support play only the MP3 core at {header.SampleRate} Hz, without the high frequencies."
                : $"The stream is MP3 Surround: {Counts.Of(stream.Bytes, "byte", "bytes")} of ancillary data carry the spatial data of its 5.1 channels. Decoders without MP3 Surround support play the stereo downmix.";
            state.Findings.AddNow(Severity.Info, name == "mp3PRO" ? "mpeg.mp3pro" : "mpeg.mp3-surround", message, node);
        }

        if (stream.Ofl is { } ofl)
        {
            int rate = ofl.IsMp3Pro ? header.SampleRate * 2 : header.SampleRate;
            Node Make(string label, NodeValue? value, string? description, string? topic = null) =>
                new(label, ofl.Offset, ofl.Length, value) { Ranges = ofl.Ranges, Description = description, Topic = topic };

            Node node = Make("Original File Length", NodeValue.Integer(ofl.Samples, "samples"), Explain.Ofl, Topics.GaplessPlayback);
            node.Add(Make("Delay", NodeValue.Integer(ofl.Delay, "samples"), Explain.OflDelay));
            node.Add(Make("Original Length", NodeValue.Integer(ofl.Samples, "samples"), Explain.OflLength));
            node.Add(Make("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.OflRate));
            node.Add(Make("Duration", Duration(ofl.Samples * (Clock / rate)), Explain.OflDuration));
            if (region.Ofl is null)
            {
                region.Ofl = node;
            }
            else
            {
                region.MoreAncillary.Add(node);
            }

            long streamSamples = stream.Samples * (rate / header.SampleRate);
            if (ofl.Delay + ofl.Samples > streamSamples)
            {
                state.Findings.AddNow(Severity.Warning, "mpeg.ofl-exceeds-stream",
                    $"The Original File Length block at 0x{ofl.Offset:X} gives a delay of {Number(ofl.Delay)} and {Counts.Of(ofl.Samples, "sample", "samples")} of audio, {Number(ofl.Delay + ofl.Samples)} samples, more than the {Number(streamSamples)} of the stream.",
                    node);
            }
        }
    }

    // What the scan keeps of one stream: its first frame with side information, the signature and
    // counts of its segments, and its audio frames.
    private sealed class StreamAncillary(MpegHeader header)
    {
        public MpegHeader Header => header;

        public Entry? First { get; set; }

        public int Segments { get; set; }

        public long Bytes { get; set; }

        // The signature of R2 and R3.
        public byte[] Signature { get; } = [0xFF, 0xFF];

        public OflBlock? Ofl { get; set; }

        public int AudioFrames { get; private set; }

        public long AudioStart { get; private set; }

        public long AudioEnd { get; private set; }

        public long Ticks { get; private set; }

        public long Samples { get; private set; }

        public void AddAudio(in Entry entry)
        {
            AudioStart = AudioFrames == 0 ? entry.Offset : AudioStart;
            AudioEnd = entry.End;
            AudioFrames++;
            Ticks += MpegAudioParser.Ticks(entry);
            Samples += new MpegHeader(entry.Header).Samples;
        }
    }

    // Ranges holds the pieces of the file that the block's bytes lie in, in file order; Offset and
    // Length cover them.
    private sealed record OflBlock(long Offset, long Length, IReadOnlyList<ByteRange> Ranges, int Delay, long Samples, bool IsMp3Pro);

    // The ancillary segments of a region's Layer III frames in reservoir order. A VBR header frame
    // keeps its place in the reservoir but has no segment; a stream change or a frame outside the
    // reservoir ends the stream, so the frame before it keeps the rest of its data, and the next
    // frame starts a stream of its own with its own signature, counts and first frame.
    private sealed class AncillaryScan(BlockReader blocks)
    {
        // Frames without data after their side information add no bytes and are not kept, so the
        // list stays bounded whatever the frame lengths.
        private readonly List<(long Stream, long File, int Length)> _areas = [];
        private StreamAncillary? _stream;
        private long _position;
        private long? _previousEnd;
        private bool _hasPrevious;
        private bool _previousIsFirst;
        private bool _previousIsTruncated;

        public List<StreamAncillary> Streams { get; } = [];

        // The bytes of all segments of the region and of its last one, the last frame's
        // ancillary data.
        public long Bytes { get; private set; }

        public long LastSegmentBytes { get; private set; }

        // The statistics' ancillary data: the bytes of the segments of the region's streams, and
        // the smallest and largest such segment that is not empty, 0 when there is none. The
        // segment of a truncated frame is left out (R5), as the audio bytes and time it is set
        // against leave the frame out, and empty segments from the smallest.
        public long AudioBytes { get; private set; }

        public long SmallestSegment { get; private set; }

        public long LargestSegment { get; private set; }

        public void Add(in Entry entry)
        {
            var header = new MpegHeader(entry.Header);
            // A truncated last frame keeps its place as long as its side information is there: its
            // main data starts in the reservoir, which ends the previous frame's segment (BladeEnc
            // 0.80 to 0.83 cut the last frame short), and it has a segment of its own where its main
            // data fits what is left of it (R1).
            bool inReservoir = header.Layer == 3 && (entry.HasSideInfo || entry.IsVbrHeader);
            if (!inReservoir || _stream is not { } stream || !stream.Header.IsSameStream(header))
            {
                Close();
                if (!inReservoir)
                {
                    return;
                }

                stream = new StreamAncillary(header);
                _stream = stream;
                Streams.Add(stream);
            }

            if (IsAudio(entry))
            {
                stream.AddAudio(entry);
            }

            long dataOffset = entry.Offset + 4 + (header.IsProtected ? 2 : 0) + Layer3SideInfo.Size(header);
            long start = _position;
            if (entry.End > dataOffset)
            {
                _areas.Add((start, dataOffset, (int)(entry.End - dataOffset)));
            }

            _position += entry.End - dataOffset;
            if (!entry.HasSideInfo)
            {
                return;
            }

            stream.First ??= entry;
            long begin = start - entry.MainDataBegin;
            long end = begin + entry.MainDataLength;

            // R1: the segment between the previous frame and this one is skipped where this
            // frame's main data starts before the stream or inside the previous frame's, and the
            // previous frame has none where its main data does not fit the reservoir as it stands
            // after it.
            if (_hasPrevious && _previousEnd is { } previousEnd && begin >= 0 && previousEnd <= begin)
            {
                Segment(previousEnd, begin, !_previousIsTruncated);
            }

            _hasPrevious = true;
            _previousIsFirst = entry.Offset == stream.First.Value.Offset;
            _previousIsTruncated = entry.IsTruncated;
            _previousEnd = begin >= 0 && end <= _position ? end : null;

            // main_data_begin reaches back at most 511 bytes.
            _areas.RemoveAll(a => a.Stream + a.Length <= start - 511);
        }

        public void Close()
        {
            if (_hasPrevious && _previousEnd is { } previousEnd)
            {
                Segment(previousEnd, _position, !_previousIsTruncated);
            }

            _areas.Clear();
            _stream = null;
            _position = 0;
            _previousEnd = null;
            _hasPrevious = false;
        }

        // One segment of the reservoir: the signature (R2), the Original File Length block of the
        // first frame's segment (R3) and the counts (R1, R5); isCounted is false for the segment
        // of a truncated frame, which R5 leaves out of the statistics.
        private void Segment(long from, long to, bool isCounted)
        {
            if (to < from || _stream is not { } stream)
            {
                return;
            }

            int length = (int)(to - from);
            stream.Segments++;
            stream.Bytes += length;
            Bytes += length;
            LastSegmentBytes = length;
            if (isCounted)
            {
                AudioBytes += length;
                if (length > 0)
                {
                    SmallestSegment = SmallestSegment == 0 ? length : Math.Min(SmallestSegment, length);
                    LargestSegment = Math.Max(LargestSegment, length);
                }
            }

            byte[] signature = stream.Signature;
            if (length == 0)
            {
                signature[0] = 0;
                signature[1] = 0;
                return;
            }

            // Only the first frame's segment can hold an OFL block, up to 20 bytes in; every other
            // segment contributes its first two.
            bool mayHoldOfl = _previousIsFirst && stream.Ofl is null;
            byte[] data = Read(from, Math.Min(length, mayHoldOfl ? 20 : 2));
            bool andFirstTwo = true;
            if (mayHoldOfl)
            {
                if (!stream.Header.IsMpeg1 && length > 20 && data[0] == 0xE0 && data[1] == 0xBA)
                {
                    int delay = data[14] << 8 | data[15];
                    long samples = (long)data[16] << 24 | (long)data[17] << 16 | (long)data[18] << 8 | data[19];
                    stream.Ofl = BlockAt(from, 20, delay, samples, true);
                }
                else if (stream.Header.IsMpeg1 && length > 9 && data[0] == 0xB4 && data[1] is 0x04 or 0x08)
                {
                    int delay = data[1] << 8 | data[2];
                    long samples = (long)data[3] << 24 | (long)data[4] << 16 | (long)data[5] << 8 | data[6];
                    stream.Ofl = BlockAt(from, 7, delay, samples, false);
                    andFirstTwo = false;
                    if (data[1] == 0x08)
                    {
                        if (length > 10)
                        {
                            signature[0] &= data[10];
                        }

                        if (length > 11)
                        {
                            signature[1] &= data[11];
                        }
                    }
                }
            }

            if (andFirstTwo)
            {
                signature[0] &= data[0];
                if (length >= 2)
                {
                    signature[1] &= data[1];
                }
            }
        }

        // The block's bytes are contiguous in the reservoir but may not be in the file: the pieces
        // of the frame data areas they lie in, as ranges.
        private OflBlock BlockAt(long position, int count, int delay, long samples, bool isMp3Pro)
        {
            var ranges = new List<ByteRange>();
            foreach ((long stream, long file, int length) in _areas)
            {
                long low = Math.Max(position, stream);
                long high = Math.Min(position + count, stream + length);
                if (low < high)
                {
                    ranges.Add(new ByteRange(file + (low - stream), high - low));
                }
            }

            return new OflBlock(ranges[0].Offset, ranges[^1].Offset + ranges[^1].Length - ranges[0].Offset, ranges, delay, samples, isMp3Pro);
        }

        private byte[] Read(long from, int count)
        {
            var bytes = new byte[count];
            int done = 0;
            foreach ((long stream, long file, int length) in _areas)
            {
                long low = Math.Max(from, stream);
                long high = Math.Min(from + count, stream + length);
                if (low < high)
                {
                    blocks.Read(file + (low - stream), (int)(high - low)).CopyTo(bytes, done);
                    done += (int)(high - low);
                }
            }

            return bytes;
        }
    }
}

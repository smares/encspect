using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// mp3PRO, MP3 Surround and the Original File Length (OFL) block, which their encoders and
// Fraunhofer's mp3sEncoder write into the ancillary data of Layer III frames. No specification is
// public, so detection follows mp3guessenc 0.27.6 (scan_ancillary_III): a frame's ancillary segment
// runs from the end of its main data, rounded up to a byte, to where the next frame's main data
// starts in the bit reservoir, and the last frame's to the end of its data. mp3PRO and MP3 Surround
// encoders start every segment with signature bits; an OFL block starts the first frame's segment.
// In mp3PRO, the OFL block counts samples at the rate an mp3PRO decoder outputs, twice the core's:
// Thomson's mp3PRO Encoder 1.1 stores 88200 samples for two seconds at 44100 Hz in a 22050 Hz
// stream (checked by hand; check_restricted_encoders.py).
public sealed partial class MpegAudioParser
{
    // A short stream would match mp3PRO's three signature bits by chance: 1 in 8 per frame.
    private const int MinEnhancedSegments = 8;

    private static void ScanAncillary(StreamState state, RegionState region)
    {
        var scan = new AncillaryScan(state.Blocks);
        foreach (Entry entry in region.Entries.Where(e => !e.IsGap))
        {
            scan.Add(entry);
        }

        scan.Close();
        region.Ancillary = scan;
        List<Entry> audio = region.Entries.Where(IsAudio).ToList();
        if (scan.First is not { } first || audio.Count == 0)
        {
            return;
        }

        var header = new MpegHeader(first.Header);
        long start = audio[0].Offset;
        long length = audio[^1].End - start;
        long ticks = audio.Sum(Ticks);
        string? name = null;
        if (scan.Segments >= MinEnhancedSegments && scan.Bytes > 0)
        {
            if (!header.IsMpeg1 && (scan.Signature[0] & 0xC0) == 0xC0 && (scan.Signature[1] & 0x08) == 0x08)
            {
                name = "mp3PRO";
            }
            else if (header.IsMpeg1 && header.SampleRate is 44100 or 48000 && (scan.Signature[0] & 0xCF) == 0xCF && (scan.Signature[1] & 0x30) == 0x30)
            {
                name = "MP3 Surround";
            }
        }

        if (name is not null)
        {
            Node node = new("Enhancement", start, length, name);
            node.Add("Ancillary Data", start, length, $"{Number(scan.Bytes)} bytes");
            node.Add("Data Rate", start, length, TenthsText(RoundHalfEven((Int128)scan.Bytes * 8 * Clock * 10, (Int128)ticks * 1000)) + " kbps");
            region.Enhancement = node;
            string message = name == "mp3PRO"
                ? $"The stream is mp3PRO: {Number(scan.Bytes)} bytes of ancillary data carry its spectral band replication data. Decoders without mp3PRO support play only the MP3 core at {header.SampleRate} Hz, without the high frequencies."
                : $"The stream is MP3 Surround: {Number(scan.Bytes)} bytes of ancillary data carry the spatial data of its 5.1 channels. Decoders without MP3 Surround support play the stereo downmix.";
            state.Findings.AddNow(Severity.Info, name == "mp3PRO" ? "mpeg.mp3pro" : "mpeg.mp3-surround", message, node);
        }

        if (scan.Ofl is { } ofl)
        {
            int rate = ofl.IsMp3Pro ? header.SampleRate * 2 : header.SampleRate;
            Node node = new("Original File Length", ofl.Offset, ofl.Length, $"{Number(ofl.Samples)} samples");
            node.Add("Delay", ofl.Offset, ofl.Length, $"{Number(ofl.Delay)} samples");
            node.Add("Original Length", ofl.Offset, ofl.Length, $"{Number(ofl.Samples)} samples");
            node.Add("Sample Rate", ofl.Offset, ofl.Length, $"{Number(rate)} Hz");
            node.Add("Duration", ofl.Offset, ofl.Length, DurationText(ofl.Samples * (Clock / rate)));
            region.Ofl = node;
            long samples = audio.Sum(e => (long)new MpegHeader(e.Header).Samples) * (rate / header.SampleRate);
            if (ofl.Delay + ofl.Samples > samples)
            {
                state.Findings.AddNow(Severity.Warning, "mpeg.ofl-exceeds-stream",
                    $"The Original File Length block at 0x{ofl.Offset:X} gives a delay of {Number(ofl.Delay)} and {Number(ofl.Samples)} samples of audio, {Number(ofl.Delay + ofl.Samples)} samples, more than the {Number(samples)} of the stream.",
                    node);
            }
        }
    }

    // The ancillary segments of a region's Layer III frames in reservoir order. A VBR header frame
    // keeps its place in the reservoir but has no segment; a stream change or a frame outside the
    // reservoir ends the stream, so the frame before it keeps the rest of its data.
    private sealed class AncillaryScan(BlockReader blocks)
    {
        private readonly List<(long Stream, long File, int Length)> _areas = [];
        private (int Version, int SampleRate)? _stream;
        private long _position;
        private long? _previousEnd;
        private bool _hasPrevious;
        private bool _previousIsFirst;

        public Entry? First { get; private set; }

        public int Segments { get; private set; }

        public long Bytes { get; private set; }

        // The bytes of the last segment, the last frame's ancillary data.
        public long LastSegmentBytes { get; private set; }

        // The AND of the first two bytes of every segment.
        public byte[] Signature { get; } = [0xFF, 0xFF];

        public (long Offset, int Length, int Delay, long Samples, bool IsMp3Pro)? Ofl { get; private set; }

        public void Add(Entry entry)
        {
            var header = new MpegHeader(entry.Header);
            bool inReservoir = header.Layer == 3 && !entry.IsTruncated && (entry.SideInfo is not null || entry.VbrFrame is not null);
            if (!inReservoir || _stream != (header.VersionBits, header.SampleRateIndex))
            {
                Close();
                if (!inReservoir)
                {
                    return;
                }

                _stream = (header.VersionBits, header.SampleRateIndex);
            }

            long dataOffset = entry.Offset + 4 + (header.IsProtected ? 2 : 0) + Layer3SideInfo.Size(header);
            long start = _position;
            _areas.Add((start, dataOffset, (int)(entry.End - dataOffset)));
            _position += entry.End - dataOffset;
            if (entry.SideInfo is null)
            {
                return;
            }

            First ??= entry;
            var info = Layer3SideInfo.Read(entry.SideInfo, header);
            long begin = start - info.MainDataBegin.Value;
            long end = begin + info.MainDataLength;

            // mp3guessenc skips the segment where this frame's main data starts before the stream or
            // inside the previous frame's, and the next one where it does not fit its frame.
            if (_hasPrevious && _previousEnd is { } previousEnd && begin >= 0 && previousEnd <= begin)
            {
                Segment(previousEnd, begin);
            }

            _hasPrevious = true;
            _previousIsFirst = entry.Offset == First.Value.Offset;
            _previousEnd = begin >= 0 && end <= _position ? end : null;

            // main_data_begin reaches back at most 511 bytes.
            _areas.RemoveAll(a => a.Stream + a.Length <= start - 511);
        }

        public void Close()
        {
            if (_hasPrevious && _previousEnd is { } previousEnd)
            {
                Segment(previousEnd, _position);
            }

            _areas.Clear();
            _stream = null;
            _position = 0;
            _previousEnd = null;
            _hasPrevious = false;
        }

        private void Segment(long from, long to)
        {
            Segments++;
            Bytes += to - from;
            LastSegmentBytes = to - from;
            if (to == from)
            {
                Signature[0] = 0;
                Signature[1] = 0;
                return;
            }

            byte[] head = Read(from, (int)Math.Min(to - from, 20));
            var header = new MpegHeader(First!.Value.Header);

            // mp3PRO: 0xE0 0xBA, then at byte 14 a 16-bit delay and a 32-bit length, in a segment of
            // more than 20 bytes; its first two bytes still carry the mp3PRO signature.
            if (_previousIsFirst && !header.IsMpeg1 && to - from > 20 && head[0] == 0xE0 && head[1] == 0xBA)
            {
                long offset = FileOffset(from);
                Ofl = (offset, (int)(FileOffset(from + 19) + 1 - offset), (head[14] << 8) | head[15],
                    ((long)head[16] << 24) | ((long)head[17] << 16) | ((long)head[18] << 8) | head[19], true);
            }

            // 0xB4, a 16-bit delay and a 32-bit length; mp3guessenc recognizes the block by the
            // delay's high byte, 0x04 for plain MP3 and 0x08 for MP3 Surround, whose signature
            // follows at bytes 10 and 11.
            if (_previousIsFirst && header.IsMpeg1 && to - from > 9 && head[0] == 0xB4 && head[1] is 0x04 or 0x08)
            {
                long offset = FileOffset(from);
                Ofl = (offset, (int)(FileOffset(from + 6) + 1 - offset), (head[1] << 8) | head[2],
                    ((long)head[3] << 24) | ((long)head[4] << 16) | ((long)head[5] << 8) | head[6], false);
                if (head[1] == 0x08)
                {
                    for (int i = 0; i < 2 && 10 + i < head.Length; i++)
                    {
                        Signature[i] &= head[10 + i];
                    }
                }

                return;
            }

            for (int i = 0; i < 2 && i < head.Length; i++)
            {
                Signature[i] &= head[i];
            }
        }

        private byte[] Read(long from, int count)
        {
            var bytes = new List<byte>(count);
            foreach ((long stream, long file, int length) in _areas)
            {
                long low = Math.Max(from, stream);
                long high = Math.Min(from + count, stream + length);
                if (low < high)
                {
                    bytes.AddRange(blocks.Read(file + (low - stream), (int)(high - low)));
                }
            }

            return [.. bytes];
        }

        private long FileOffset(long position)
        {
            (long stream, long file, int _) = _areas.Last(a => a.Stream <= position);
            return file + (position - stream);
        }
    }
}

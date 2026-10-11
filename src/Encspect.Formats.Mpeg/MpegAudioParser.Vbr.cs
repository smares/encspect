using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// Xing, Info, LAME and VBRI headers, their CRCs, and which encoder a region's frames point to.
public sealed partial class MpegAudioParser
{
    private const int MusicCrcChunk = 1 << 16;

    private static readonly byte[] LameSignature = "LAME"u8.ToArray();

    // Gogo-no-coda fills the bits no frame uses with "GOGO" over and over; two in a row rule out chance.
    private static readonly byte[] GogoSignature = "GOGOGOGO"u8.ToArray();

    private static bool HasVbrMagic(BlockReader blocks, long position, long available, MpegHeader header)
    {
        if (header.Layer != 3)
        {
            return false;
        }

        return MagicAt(36, "VBRI") || MagicAt(VbrHeader.XingOffset(header), "Xing") || MagicAt(VbrHeader.XingOffset(header), "Info");

        bool MagicAt(int offset, string magic) =>
            offset + 4 <= available && Enumerable.Range(0, 4).All(i => blocks.ByteAt(position + offset + i) == magic[i]);
    }

    private static void ReportVbrHeader(StreamState state, Entry frame, VbrHeader vbr, bool midStream)
    {
        FindingCollector findings = state.Findings;
        long position = frame.Offset;
        Node node = FindingNode(frame);
        if (vbr.IsTruncated)
        {
            findings.Add(Severity.Warning, "mpeg.vbr-header-truncated", node, count =>
                $"{Frames(count)} hold a {vbr.Type} header that does not fit the frame, the first at 0x{position:X}.");
        }

        if (vbr.InvalidEntrySize is { } entrySize)
        {
            findings.Add(Severity.Warning, "mpeg.vbri-entry-size-invalid", node, count =>
                $"{Frames(count)} hold a VBRI header whose TOC entries are {entrySize} bytes long, where only 1 to 4 bytes are defined, so its TOC is not read; the first at 0x{position:X}.");
        }

        if (vbr.VbriVersion is { } version and not 1)
        {
            findings.Add(Severity.Warning, "mpeg.vbri-version-unknown", node, count =>
                $"{Frames(count)} hold a VBRI header of version {version}, where only version 1 is known; ffmpeg ignores the header. The first at 0x{position:X}.");
        }

        if (vbr.IsTocDecreasing)
        {
            findings.Add(Severity.Warning, "mpeg.xing-toc-decreasing", node, count =>
                $"{Frames(count)} hold a Xing TOC that decreases, so seeking by it goes backwards; the first at 0x{position:X}.");
        }

        if (vbr.HasLameTag && !vbr.IsTagCrcValid)
        {
            if (vbr.StoredTagCrc == vbr.FfmpegTagCrc)
            {
                // The encoder's fault, not damage, but the CRC is still wrong and readers act on it.
                findings.Add(Severity.Warning, "mpeg.lame-tag-crc-ffmpeg", node, count =>
                    $"The LAME tag CRC of {Frames(count)} covers the frame's first 190 bytes, as ffmpeg writes it, instead of the bytes before the CRC; LAME and ffmpeg itself reject it, so readers that check it ignore the tag's encoder string and ReplayGain. The first at 0x{position:X}.");
            }
            else
            {
                findings.Add(Severity.Warning, "mpeg.lame-tag-crc-mismatch", node, count =>
                    $"The LAME tag of {Frames(count)} fails its CRC check, so its fields may be damaged; the first at 0x{position:X}.");
            }
        }

        if (midStream)
        {
            findings.Add(Severity.Warning, "mpeg.vbr-header-mid-stream", node, count =>
                $"{Frames(count)} in the middle of a stream hold a {vbr.Type} header, probably where files were joined; players that only look at the first frame play them as silence. The first at 0x{position:X}.");
        }

        if (vbr.HasLameTag)
        {
            state.LameTags.Add((frame, vbr));
        }
    }

    // The music CRC covers the bytes after the tag's frame up to the music length, counted from
    // the start of that frame (VbrTag.c: the Xing byte count, which includes the frame).
    private static void CheckMusicCrcs(StreamState state)
    {
        Dictionary<long, int> running = RunningCrcs(state);
        foreach ((Entry frame, VbrHeader vbr) in state.LameTags)
        {
            long position = frame.Offset;
            long end = frame.Offset + vbr.MusicLength;
            Node node = FindingNode(frame);
            if (end < frame.End)
            {
                long musicLength = vbr.MusicLength;
                state.Findings.Add(Severity.Warning, "mpeg.music-length-short", node, count =>
                    $"The LAME tag of {Frames(count)} gives a music length of {Number(musicLength)} bytes, shorter than its own frame, so the music CRC is not verified; the first at 0x{position:X}.");
                continue;
            }

            if (end > state.Blocks.End)
            {
                state.Findings.Add(Severity.Warning, "mpeg.music-crc-not-verified", node, count =>
                    $"The LAME tag of {Frames(count)} covers more music than the file holds after it, so the music CRC is not verified; the first at 0x{position:X}.");
                continue;
            }

            int crc = running[end] ^ VbrHeader.LameCrcShift(running[frame.End], end - frame.End);
            state.Build.MusicCrcs[position] = crc;
            if (crc == vbr.StoredMusicCrc)
            {
                continue;
            }

            if (vbr.IsNoGapContinuation)
            {
                // Likely LAME's doing, but the CRC is wrong all the same, and an edit of the audio
                // after encoding would look no different.
                state.Findings.Add(Severity.Warning, "mpeg.music-crc-nogap", node, count =>
                    $"The music CRC in the LAME tag of {Frames(count)} does not match. The file continues a gapless (--nogap) sequence, and LAME continues the previous file's CRC there, so the CRC is probably wrong from encoding; either way it cannot confirm that the audio is unchanged. The first at 0x{position:X}.");
            }
            else
            {
                state.Findings.Add(Severity.Warning, "mpeg.music-crc-mismatch", node, count =>
                    $"The audio after the LAME tag of {Frames(count)} no longer matches the tag's music CRC: it changed after encoding, as MP3Gain and other editors change it. The first at 0x{position:X}.");
            }
        }
    }

    // The running CRC from the first range's start at every start and end of a music CRC range,
    // in one pass over the file however many LAME tags there are and however their ranges overlap.
    private static Dictionary<long, int> RunningCrcs(StreamState state)
    {
        var positions = new SortedSet<long>();
        foreach ((Entry frame, VbrHeader vbr) in state.LameTags)
        {
            long end = frame.Offset + vbr.MusicLength;
            if (end >= frame.End && end <= state.Blocks.End)
            {
                positions.Add(frame.End);
                positions.Add(end);
            }
        }

        var running = new Dictionary<long, int>();
        int crc = 0;
        long at = positions.Count > 0 ? positions.Min : 0;
        foreach (long position in positions)
        {
            for (; at < position; at += Math.Min(MusicCrcChunk, position - at))
            {
                crc = VbrHeader.LameCrc(state.Blocks.Read(at, (int)Math.Min(MusicCrcChunk, position - at)), crc);
            }

            running[position] = crc;
        }

        return running;
    }

    // The encoder a region's frames point to: the encoder string of a LAME tag, Fraunhofer for a
    // VBRI header, and "LAME" with its version where LAME wrote them into ancillary data, which it
    // does with the bits no frame uses (libmp3lame/bitstream.c, drain_into_ancillary). The
    // ancillary data alone is low confidence; ffmpeg's tag names ffmpeg, which leaves the encoder
    // behind it to the ancillary data. MP3 Surround is Fraunhofer's, mp3PRO encoders came from
    // several licensees, and the OFL block is written by Fraunhofer's encoders.
    private static Node? DetectEncoder(StreamState state, RegionState region)
    {
        List<Entry> entries = region.Entries;
        Entry tagFrame = entries.FirstOrDefault(e => e.VbrFrame is not null);
        VbrHeader? vbr = tagFrame.VbrFrame is null ? null : VbrHeader.TryRead(tagFrame.VbrFrame, new MpegHeader(tagFrame.Header));
        List<Node> vbrNodes = vbr?.ToNodes(tagFrame.Offset, 0, null).ToList() ?? [];
        Node? tagNode = vbrNodes.FirstOrDefault(n => n.Name is "VBRI" or "LAME Tag" or "Encoder String");
        string? enhancement = region.Enhancement?.Value;
        var ancillaryEvidence = new List<Node>();
        if (region.Enhancement is { } enhanced)
        {
            ancillaryEvidence.Add(Evidence(enhanced, $"{enhanced.Value} signature in the ancillary data of every frame"));
        }

        if (region.Ofl is { } ofl)
        {
            ancillaryEvidence.Add(Evidence(ofl, "Original File Length block"));
        }

        if (vbr is { IsVbri: true })
        {
            string name = enhancement switch
            {
                "MP3 Surround" => "Fraunhofer MP3 Surround encoder",
                "mp3PRO" => "Fraunhofer mp3PRO encoder",
                _ => "Fraunhofer",
            };
            return Encoder(name, "High", [Evidence(tagNode!, "VBRI header"), .. ancillaryEvidence]);
        }

        (long Offset, int Length, string Text)? signature = FindSignature(state, entries);
        Node? ancillary = signature is { } found ? new Node("Evidence", found.Offset, found.Length, $"\"{found.Text}\" in ancillary data") : null;

        if (vbr is { HasLameTag: true })
        {
            string text = vbr.LameEncoder;
            if (LameName(text) is { } name)
            {
                Node tagEvidence = vbr.IsTagCrcValid ? Evidence(tagNode!, "LAME tag") : Evidence(tagNode!, "LAME tag, whose CRC fails");
                string confidence = vbr.IsTagCrcValid ? "High" : "Medium";

                // A version longer than the tag's nine bytes ends in a cut-off "." there
                // ("LAME3.90." of HydrogenAudio's 3.90.3); the padding holds it whole, as getID3 reads it.
                if (text.EndsWith('.') && signature is { Text: var whole } && whole.Length > text.Length
                    && whole.StartsWith(text, StringComparison.Ordinal) && whole[text.Length..].All(char.IsAsciiDigit))
                {
                    return Encoder(LameName(whole)!, confidence, tagEvidence, ancillary!);
                }

                return Encoder(name, confidence, tagEvidence);
            }

            bool ffmpeg = text.StartsWith("Lavc", StringComparison.Ordinal) || text.StartsWith("Lavf", StringComparison.Ordinal);
            string writer = ffmpeg ? $"FFmpeg ({text})" : text;
            if (ffmpeg && ancillary is not null)
            {
                return Encoder(SignatureName(signature!.Value.Text), "Medium", ancillary, Evidence(tagNode!, $"LAME tag written by {writer}"));
            }

            // ffmpeg encodes MP3 with libmp3lame or libshine, so Shine's traits behind its tag name Shine.
            if (ffmpeg && TraitEncoder(region) is { Value: "Shine" } shine)
            {
                return Encoder("Shine", "Medium", shine.Children.Single(c => c.Name == "Evidence"), Evidence(tagNode!, $"LAME tag written by {writer}"));
            }

            return Encoder(writer, "Medium", Evidence(tagNode!, "LAME tag"));
        }

        if (vbr is { HasGogoSignature: true })
        {
            Node quality = vbrNodes.First(n => n.Name is "Xing" or "Info").Children.First(c => c.Name == "Quality");
            Node xing = Evidence(quality, "\"GOGO\" in the Xing header's quality field");
            return signature is { Text: "GOGO" } ? Encoder("Gogo", "Medium", xing, ancillary!) : Encoder("Gogo", "Medium", xing);
        }

        if (vbr?.EncoderString is { } old && LameName(old) is { } oldName)
        {
            return Encoder(oldName, "Medium", Evidence(tagNode!, "Version string after the Xing header"));
        }

        if (ancillaryEvidence.Count > 0)
        {
            string name = enhancement switch
            {
                "MP3 Surround" => "Fraunhofer MP3 Surround encoder",
                "mp3PRO" => "mp3PRO encoder",
                _ => "Fraunhofer",
            };
            return Encoder(name, "Medium", [.. ancillaryEvidence]);
        }

        return ancillary is null ? TraitEncoder(region) : Encoder(SignatureName(signature!.Value.Text), "Low", ancillary);

        static Node Evidence(Node at, string text) => new("Evidence", at.Offset, at.Length, text);

        static Node Encoder(string name, string confidence, params Node[] evidence)
        {
            long start = evidence.Min(e => e.Offset);
            long end = evidence.Max(e => e.End);
            Node node = new("Encoder", start, end - start, name);
            node.Add("Confidence", start, end - start, confidence);
            foreach (Node item in evidence)
            {
                node.Add(item);
            }

            return node;
        }
    }

    private static string SignatureName(string text) => text == "GOGO" ? "Gogo" : LameName(text)!;

    // "LAME3.100" as "LAME 3.100"; LAME 3.99 alphas wrote "L3.99" without "AME". The maintained Helix
    // encoder writes a LAME tag with "LAMEH" and its version (hmp3 5.2.4: "LAMEH5.24"). LAME pads a
    // short version to the field's nine bytes with spaces ("LAME3.91 ").
    private static string? LameName(string text)
    {
        text = text.TrimEnd(' ');
        if (text.StartsWith("LAMEH", StringComparison.Ordinal))
        {
            return text.Length > 5 ? $"Helix MP3 Encoder {text[5..]}" : "Helix MP3 Encoder";
        }

        if (text.StartsWith("LAME", StringComparison.Ordinal))
        {
            return text.Length > 4 ? $"LAME {text[4..]}" : "LAME";
        }

        return text.Length >= 5 && text[0] == 'L' && char.IsAsciiDigit(text[1]) && text[2] == '.' ? $"LAME {text[1..]}" : null;
    }

    // The first "LAME" with a version in the ancillary data of the region's Layer III frames, at
    // any bit position: ancillary data starts where the main data ends, which need not be a byte
    // boundary, and runs on into the next frame's data when that frame's main data starts later, so
    // the pieces are searched joined. Without one, the first "LAME" alone, which LAME writes when
    // fewer than 32 bits follow it.
    private static (long Offset, int Length, string Text)? FindSignature(StreamState state, List<Entry> entries)
    {
        List<MainDataRun> runs = state.Build.Runs;
        var ancillary = new AncillaryStream();
        foreach (Entry frame in entries.Where(e => e.SideInfo is not null))
        {
            long dataStart = frame.Offset + 4 + (frame.Crc >= 0 ? 2 : 0) + frame.SideInfo!.Length;
            long cursor = dataStart;
            int low = 0;
            int high = runs.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                (low, high) = runs[middle].End <= dataStart ? (middle + 1, high) : (low, middle);
            }

            for (int i = low; ; i++)
            {
                long next = i < runs.Count && runs[i].Offset < frame.End ? runs[i].Offset : frame.End;
                if (next > cursor || cursor == frame.End)
                {
                    // The last byte of the main data before the ancillary data may hold its first bits.
                    long from = cursor > dataStart ? cursor - 1 : cursor;
                    if (cursor != dataStart || !ancillary.EndsFrame)
                    {
                        ancillary.Search();
                        if ((ancillary.Found ?? ancillary.Gogo) is { } found)
                        {
                            return found;
                        }

                        ancillary.Clear();
                    }

                    ancillary.Append(state.Blocks.Read(from, (int)(next - from)), from, next == frame.End);
                }

                if (next >= frame.End)
                {
                    break;
                }

                cursor = runs[i].End;
            }
        }

        ancillary.Search();
        return ancillary.Found ?? ancillary.Gogo ?? ancillary.Plain;
    }

    // Ancillary bytes that follow each other in the main-data byte stream, with where each piece
    // lies in the file. Searched in slices, so a long stretch does not stay in memory.
    private sealed class AncillaryStream
    {
        private const int SliceLength = 1 << 16;

        private readonly List<byte> _bytes = [];
        private readonly List<(int Index, long Offset)> _pieces = [];

        // Whether the last piece reaches the end of its frame, so the next frame's data continues it.
        public bool EndsFrame { get; private set; }

        // The first "LAME" with a version, and the first without one.
        public (long Offset, int Length, string Text)? Found { get; private set; }

        public (long Offset, int Length, string Text)? Plain { get; private set; }

        public (long Offset, int Length, string Text)? Gogo { get; private set; }

        public void Append(byte[] data, long offset, bool endsFrame)
        {
            _pieces.Add((_bytes.Count, offset));
            _bytes.AddRange(data);
            EndsFrame = endsFrame;
            if (_bytes.Count > SliceLength)
            {
                Search();
                Keep(LameSignature.Length + 20);
            }
        }

        public void Clear()
        {
            _bytes.Clear();
            _pieces.Clear();
            EndsFrame = false;
        }

        public void Search()
        {
            if (_bytes.Count < LameSignature.Length)
            {
                return;
            }

            byte[] data = _bytes.ToArray();
            if (Gogo is null && SearchFill(data, GogoSignature) is { } gogo)
            {
                Gogo = Locate(gogo.Offset, gogo.Length, "GOGO");
            }

            if (Found is not null || MpegAudioParser.Search(data, 0) is not { } found)
            {
                return;
            }

            var located = Locate(found.Offset, found.Length, found.Text);
            if (found.Text.Length > LameSignature.Length)
            {
                Found = located;
            }
            else
            {
                Plain ??= located;
            }
        }

        private (long Offset, int Length, string Text) Locate(long index, int length, string text)
        {
            long start = FileOffset((int)index);
            long end = FileOffset((int)index + length - 1) + 1;
            return (start, (int)(end - start), text);
        }

        private long FileOffset(int index)
        {
            (int Index, long Offset) piece = _pieces.Last(p => p.Index <= index);
            return piece.Offset + (index - piece.Index);
        }

        // Drops all but the last count bytes, which a signature may start in.
        private void Keep(int count)
        {
            int drop = _bytes.Count - count;
            long offset = FileOffset(drop);
            _bytes.RemoveRange(0, drop);
            var pieces = _pieces.Where(p => p.Index > drop).Select(p => (p.Index - drop, p.Offset)).ToList();
            _pieces.Clear();
            _pieces.Add((0, offset));
            _pieces.AddRange(pieces);
        }
    }

    private static (long Offset, int Length, string Text)? Search(byte[] data, long offset)
    {
        (long Offset, int Length, string Text)? plain = null;
        for (int shift = 0; shift < 8; shift++)
        {
            byte[] shifted = shift == 0 ? data : Shift(data, shift);
            for (int start = shifted.AsSpan().IndexOf(LameSignature); start >= 0;)
            {
                // The version runs to the first byte that is not printable, another "L", or LAME's
                // alternating fill, 0x55 or 0xAA (as mp3guessenc reads it).
                int end = start + LameSignature.Length;
                while (end < shifted.Length && end - start < 20 && shifted[end] is >= 32 and < 127 and not (byte)'L' and not 0x55)
                {
                    end++;
                }

                int bits = shift + (8 * (end - start));
                var found = (offset + start, (bits + 7) / 8, Encoding.ASCII.GetString(shifted, start, end - start));
                if (end - start > LameSignature.Length)
                {
                    return found;
                }

                plain ??= found;
                int next = shifted.AsSpan(start + 1).IndexOf(LameSignature);
                start = next < 0 ? -1 : start + 1 + next;
            }
        }

        return plain;
    }

    // The first place pattern starts in data at any bit position: its byte index and the number of
    // bytes it touches.
    private static (long Offset, int Length)? SearchFill(byte[] data, byte[] pattern)
    {
        for (int shift = 0; shift < 8 && data.Length > pattern.Length; shift++)
        {
            byte[] shifted = shift == 0 ? data : Shift(data, shift);
            int start = shifted.AsSpan().IndexOf(pattern);
            if (start >= 0)
            {
                return (start, (shift + (8 * pattern.Length) + 7) / 8);
            }
        }

        return null;
    }

    // The bytes of data read from bit shift onwards.
    private static byte[] Shift(byte[] data, int shift)
    {
        var shifted = new byte[data.Length - 1];
        for (int i = 0; i < shifted.Length; i++)
        {
            shifted[i] = (byte)((data[i] << shift) | (data[i + 1] >> (8 - shift)));
        }

        return shifted;
    }
}

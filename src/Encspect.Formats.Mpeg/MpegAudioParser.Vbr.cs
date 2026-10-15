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

    private static void ReportVbrHeader(StreamState state, int index, Entry frame, VbrHeader vbr, bool midStream)
    {
        FindingCollector findings = state.Findings;
        long position = frame.Offset;
        Node node = FindingNode(frame);
        if (vbr.IsTruncated)
        {
            findings.Add(Severity.Warning, "mpeg.vbr-header-truncated", node, count =>
                $"{Frames(count, "holds", "hold")} {Article(vbr.Type)} {vbr.Type} header that does not fit the frame, the first at 0x{position:X}.");
        }

        if (vbr.InvalidEntrySize is { } entrySize)
        {
            findings.Add(Severity.Warning, "mpeg.vbri-entry-size-invalid", node, count =>
                $"{Frames(count, "holds", "hold")} a VBRI header whose TOC entries are {entrySize} bytes long, where only 1 to 4 bytes are defined, so its TOC is not read; the first at 0x{position:X}.");
        }

        if (vbr.VbriVersion is { } version and not 1)
        {
            findings.Add(Severity.Warning, "mpeg.vbri-version-unknown", node, count =>
                $"{Frames(count, "holds", "hold")} a VBRI header of version {version}, where only version 1 is known; ffmpeg ignores the header. The first at 0x{position:X}.");
        }

        if (vbr.IsTocDecreasing)
        {
            findings.Add(Severity.Warning, "mpeg.xing-toc-decreasing", node, count =>
                $"{Frames(count, "holds", "hold")} a Xing TOC that decreases, so seeking by it goes backwards; the first at 0x{position:X}.");
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
                $"{Frames(count)} in the middle of a stream {(count == 1 ? "holds" : "hold")} {Article(vbr.Type)} {vbr.Type} header, probably where files were joined; players that only look at the first frame play {(count == 1 ? "it" : "them")} as silence. The first at 0x{position:X}.");
        }

        if (vbr.HasLameTag)
        {
            state.LameTags.Add((index, vbr));
        }
    }

    // The music CRC covers the bytes after the tag's frame up to the music length, counted from
    // the start of that frame (VbrTag.c: the Xing byte count, which includes the frame).
    private static void CheckMusicCrcs(StreamState state)
    {
        Dictionary<long, int> running = RunningCrcs(state);
        foreach ((int index, VbrHeader vbr) in state.LameTags)
        {
            Entry frame = state.Build.Frames[index];
            long position = frame.Offset;
            long end = frame.Offset + vbr.MusicLength;
            Node node = FindingNode(frame);
            if (vbr.MusicLength == 0)
            {
                state.Findings.Add(Severity.Info, "mpeg.music-length-not-set", node, count =>
                    $"The LAME tag of {Frames(count)} leaves the music length unset, which the tag's specification allows, so the music CRC is not verified; the first at 0x{position:X}.");
                continue;
            }

            if (end < frame.End)
            {
                long musicLength = vbr.MusicLength;
                state.Findings.Add(Severity.Warning, "mpeg.music-length-short", node, count =>
                    $"The LAME tag of {Frames(count)} gives a music length of {Number(musicLength)} bytes, shorter than its own frame, so the music CRC is not verified; the first at 0x{position:X}.");
                continue;
            }

            if (end > state.Blocks.End)
            {
                // A stream cut short is reported by its own findings; otherwise the tag is wrong.
                if (IsCutShort(frame, vbr, Covered(state, index)))
                {
                    state.Findings.Add(Severity.Info, "mpeg.music-crc-not-verified", node, count =>
                        $"The LAME tag of {Frames(count)} covers more music than the file holds after it, which is cut short, so the music CRC is not verified; the first at 0x{position:X}.");
                }
                else
                {
                    long musicLength = vbr.MusicLength;
                    state.Findings.Add(Severity.Warning, "mpeg.music-length-long", node, count =>
                        $"The LAME tag of {Frames(count)} gives a music length of {Number(musicLength)} bytes, more than the file holds after it, though the stream holds every frame its header counts; the music CRC is not verified. The first at 0x{position:X}.");
                }

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
        foreach ((int index, VbrHeader vbr) in state.LameTags)
        {
            Entry frame = state.Build.Frames[index];
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

    // The encoder a region's frames point to. A VBR header names the encoder of the frames it
    // describes (Covered: up to the next header or a change of stream), so only a header in the
    // region's first frame names the region's encoder from its tag: a LAME tag (high confidence
    // when its CRC holds, medium when not), Fraunhofer for a VBRI header. Where the region also
    // holds frames that header does not describe, or later headers that name another encoder,
    // files were joined and the name is at most medium confidence. A header further on names no
    // more than the frames after it: the region's encoder comes from the other evidence, which a
    // header that names the same encoder supports, or, without any, from that header with low
    // confidence. LAME writes "LAME" and its version into the bits no frame uses
    // (libmp3lame/bitstream.c, drain_into_ancillary): alone low confidence, together with the tag
    // the more specific of the two versions. ffmpeg writes a LAME tag into every MP3 it muxes, with
    // the encoder string of the stream it copies or its own: "Lavc" when libavcodec encoded, which
    // it does with libmp3lame or libshine, and "Lavf" when it only muxed, which names no encoder.
    // MP3 Surround is Fraunhofer's, mp3PRO encoders came from several licensees, and the OFL block
    // is written by Fraunhofer's encoders.
    private static Node? DetectEncoder(StreamState state, RegionState region)
    {
        ChunkedList<Entry> frames = state.Build.Frames;
        int end = region.End ?? frames.Count;
        var headers = new List<HeaderFrame>();
        for (int i = region.First; i < end; i++)
        {
            state.CheckCancellation(i - region.First);
            if (frames[i].IsVbrHeader)
            {
                headers.Add(new HeaderFrame(i, frames[i], ReadVbrHeader(state.Build, frames[i])));
            }
        }

        var context = new EncoderContext(state, region, FindSignature(state, region));
        if (headers.Count == 0)
        {
            return StreamEncoder(context);
        }

        if (headers[0].Index != FirstFrame(frames, region))
        {
            Node? found = StreamEncoder(context);
            var named = headers.Where(h => TagEncoder(h) is not null).ToList();
            if (named.Count == 0)
            {
                return found;
            }

            string name = found?.Value?.ToString() ?? TagEncoder(named[0])!.Value.Name;
            var same = named.Where(h => TagEncoder(h)!.Value.Name == name || FamiliesOf(name).Overlaps(FamiliesOf(TagEncoder(h)!.Value.Name))).ToList();
            if (same.Count == 0)
            {
                return found;
            }

            string what = $"{TagEncoder(same[0])!.Value.What} at 0x{same[0].Frame.Offset:X}";
            Node evidence = Evidence(same[0].TagNode, same[^1].TagNode, same.Count == 1
                ? $"{what}, which describes only the {FramesText(Covered(state, same[0].Index).Frames)} after it"
                : $"{what} and {Number(same.Count - 1)} more headers, which describe only the frames after them");
            return found is null ? Encoder(name, "Low", evidence) : WithEvidence(found, evidence, null);
        }

        HeaderFrame leading = headers[0];
        Node? encoder = HeaderEncoder(context, leading);
        if (encoder is null)
        {
            return null;
        }

        // The headers' frames follow each other, each up to the next header.
        List<Coverage> described = headers.Select(h => Covered(state, h.Index)).Where(c => c.Frames > 0).ToList();
        bool isAllDescribed = true;
        int audio = 0;
        for (int i = region.First, next = 0; i < end; i++)
        {
            if (frames[i].IsGap || frames[i].IsVbrHeader)
            {
                continue;
            }

            audio++;
            while (next < described.Count && described[next].End <= i)
            {
                next++;
            }

            isAllDescribed &= next < described.Count && described[next].FirstFrame <= i;
        }

        string? leadingName = TagEncoder(leading)?.Name;
        bool joined = headers.Skip(1).Any(h => TagEncoder(h)?.Name != leadingName);
        if (!joined && isAllDescribed)
        {
            // Headers without a tag (Xing's, ffmpeg's "Lavf") name nothing to agree on.
            if (headers.Count == 1 || leadingName is null)
            {
                return encoder;
            }

            // Files of one encoder joined, each with its header.
            HeaderFrame next = headers[1];
            string what = $"{TagEncoder(next)?.What ?? next.Vbr.Type + " header"} at 0x{next.Frame.Offset:X}";
            bool damaged = headers.Skip(1).Any(h => h.Vbr.HasLameTag && !h.Vbr.IsTagCrcValid);
            return WithEvidence(encoder, Evidence(next.TagNode, headers[^1].TagNode, headers.Count == 2
                ? $"{what}, which names the same encoder for the frames after it"
                : $"{what} and {Number(headers.Count - 2)} more headers, which name the same encoder for the frames after them"), damaged ? "Medium" : null);
        }

        Coverage covered = Covered(state, leading.Index);
        (int own, long from, long span) = AudioIn(state, region, covered);
        Node scope = own > 0 ? new Node("Evidence", from, span) { Description = Explain.Evidence } : leading.TagNode;
        return WithEvidence(encoder, Evidence(scope, $"The {leading.Vbr.Type} header describes only the {FramesText(covered.Frames)} after it; the region holds {FramesText(audio)}, so files were probably joined"),
            "Medium");
    }

    // The encoder node with more evidence, spanning it too, and its confidence at most cap.
    private static Node WithEvidence(Node encoder, Node evidence, string? cap)
    {
        string confidence = encoder.Children.First(c => c.Name == "Confidence").Value!.ToString();
        if (cap == "Medium" && confidence == "High")
        {
            confidence = "Medium";
        }

        return Encoder(encoder.Value!.ToString(), confidence, [.. encoder.Children.Where(c => c.Name == "Evidence"), evidence]);
    }

    // The encoder named by the header in the region's first frame, with the stream's evidence.
    private static Node? HeaderEncoder(EncoderContext context, HeaderFrame header)
    {
        VbrHeader vbr = header.Vbr;
        Node tagNode = header.TagNode;
        if (vbr.IsVbri)
        {
            string name = context.Enhancement switch
            {
                "MP3 Surround" => "Fraunhofer MP3 Surround encoder",
                "mp3PRO" => "Fraunhofer mp3PRO encoder",
                _ => "Fraunhofer",
            };
            return Encoder(name, "High", [Evidence(tagNode, "VBRI header"), .. context.AncillaryEvidence]);
        }

        if (vbr is { HasLameTag: true } && !IsFfmpeg(vbr.LameEncoder))
        {
            Node tagEvidence = vbr.IsTagCrcValid ? Evidence(tagNode, "LAME tag") : Evidence(tagNode, "LAME tag, whose CRC fails");
            string confidence = vbr.IsTagCrcValid ? "High" : "Medium";
            string text = vbr.LameEncoder.TrimEnd(' ');
            if (text.StartsWith("LAMEH", StringComparison.Ordinal))
            {
                return Encoder(text.Length > 5 ? $"Helix MP3 Encoder {text[5..]}" : "Helix MP3 Encoder", confidence, tagEvidence);
            }

            LameRelease? tagged = LameRelease.Parse(vbr.LameEncoder);
            if (tagged is not null && context.Signature is { IsCut: false } signature && LameRelease.Parse(signature.Text) is { } drained)
            {
                if (tagged.Agrees(drained))
                {
                    // The tag holds nine bytes, too few for some versions ("LAME3.99r" of 3.99.5); the
                    // ancillary data holds LAME's whole short version. Where it has a marker, it
                    // confirms the tag's alpha, beta or release.
                    if (drained.Kind != "plain")
                    {
                        return Encoder(drained.IsMoreSpecificThan(tagged) ? drained.Name : tagged.Name, confidence, tagEvidence, context.Ancillary!);
                    }
                }
                else
                {
                    string tagName = tagged.Name;
                    long position = header.Frame.Offset;
                    context.State.Findings.Add(Severity.Warning, "mpeg.lame-version-differs", FindingNode(header.Frame), count =>
                        $"The LAME tag at 0x{position:X} names {tagName}, but the version LAME wrote into the ancillary data names {drained.Name}: the frames and the tag may come from different encoders. The tag's version is shown.");
                }
            }

            return Encoder(tagged?.Name ?? LameName(text) ?? text, confidence, tagEvidence);
        }

        if (vbr is { HasLameTag: true })
        {
            // libavcodec encodes MP3 only with libmp3lame or libshine, so "Lavc" supports either; a
            // remux ("Lavf") names no encoder, and the stream's evidence stands as it would alone. The tag
            // names FFmpeg's muxer either way, which the writer is resolved from (DetectWriter).
            string text = vbr.LameEncoder.TrimEnd(' ');
            bool encoded = text.StartsWith("Lavc", StringComparison.Ordinal);
            Node support = Evidence(tagNode, $"LAME tag written by FFmpeg ({text})");
            if (StreamEncoder(context) is { } behind)
            {
                if (!encoded)
                {
                    return behind;
                }

                behind = WithEvidence(behind, support, null);
                Node confidence = behind.Children.First(c => c.Name == "Confidence");
                if (encoded && confidence.Value == "Low" && (behind.Value == "Shine" || behind.Value!.ToString().StartsWith("LAME", StringComparison.Ordinal)))
                {
                    confidence.Value = "Medium";
                }

                return behind;
            }

            return encoded ? Encoder($"FFmpeg ({text})", "Medium", Evidence(tagNode, "LAME tag")) : null;
        }

        if (vbr is { HasGogoSignature: true })
        {
            Node quality = header.Nodes.First(n => n.Name is "Xing" or "Info").Children.First(c => c.Name == "Quality");
            Node xing = Evidence(quality, "\"GOGO\" in the Xing header's quality field");
            return context.Signature is { Text: "GOGO" } ? Encoder("Gogo", "Medium", xing, context.Ancillary!) : Encoder("Gogo", "Medium", xing);
        }

        if (vbr.EncoderString is { } old && LameName(old) is { } oldName)
        {
            return Encoder(oldName, "Medium", Evidence(tagNode, "Version string after the Xing header"));
        }

        return StreamEncoder(context);
    }

    // The writer a region's claims and its LAME tags name. ffmpeg's mp3 muxer writes the whole Xing and LAME tag (libavformat/mp3enc.c),
    // so a tag laid out as it writes it is the muxer's: an encoder string that starts "Lavc" (the stream's "encoder" metadata, the
    // codec library's version, which names the encoder: HeaderEncoder) or "Lavf" ("Lavf lame" in place of the bit-exact "Lavc libmp3lame",
    // mp3enc.c:250-252, or a bare "Lavf" without metadata, mp3enc.c:258), or the CRC over the frame's first 190 bytes that only ffmpeg
    // writes. It names the writer "FFmpeg" without a version, since the "Lavc" version is the codec library's, with low confidence.
    // Tags of the claims' kind (ID3v2 TSSE, APE) are weighed with it.
    private static Node? DetectWriter(StreamState state, RegionState region, IReadOnlyList<EncoderClaim> claims)
    {
        ChunkedList<Entry> frames = state.Build.Frames;
        int end = region.End ?? frames.Count;
        var all = new List<EncoderClaim>(claims);
        for (int i = region.First; i < end; i++)
        {
            state.CheckCancellation(i - region.First);
            if (!frames[i].IsVbrHeader)
            {
                continue;
            }

            var header = new HeaderFrame(i, frames[i], ReadVbrHeader(state.Build, frames[i]));
            if (header.Vbr is { HasLameTag: true, IsVbri: false } lame)
            {
                string text = lame.LameEncoder.TrimEnd(' ');
                bool quirk = lame.FfmpegTagCrc >= 0 && !lame.IsTagCrcValid && lame.StoredTagCrc == lame.FfmpegTagCrc;
                if (IsFfmpeg(text) || quirk)
                {
                    all.Add(new EncoderClaim("FFmpeg", header.TagNode,
                        $"LAME tag laid out as ffmpeg's mp3 muxer writes it (libavformat/mp3enc.c writes the whole tag), with the encoder string \"{text}\"{(quirk ? " and ffmpeg's tag CRC" : string.Empty)}", ClaimRole.Writer)
                    {
                        IsLayout = true,
                    });
                }
            }
        }

        if (region.AudioFrames == 0 || !ClaimRoles.Writers(all).Any())
        {
            return null;
        }

        long offset = region.AudioStart;
        long length = region.AudioEnd - offset;
        return WriterDetection.Detect(all, (name, value, description) => new Node(name, offset, length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => state.Findings.Add(Severity.Info, "mpeg.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            format: new WriterDetection.StreamFormat(KnownWriters.MpegAudio, "MPEG audio", (claim, message, node) => state.Findings.Add(Severity.Info, "mpeg.writer-other-format", node, _ => message)));
    }

    // The encoder named by the stream's own evidence, without its VBR headers.
    private static Node? StreamEncoder(EncoderContext context)
    {
        if (context.AncillaryEvidence.Count > 0)
        {
            string name = context.Enhancement switch
            {
                "MP3 Surround" => "Fraunhofer MP3 Surround encoder",
                "mp3PRO" => "mp3PRO encoder",
                _ => "Fraunhofer",
            };
            return Encoder(name, "Medium", [.. context.AncillaryEvidence]);
        }

        return context.Signature is { } signature ? Encoder(SignatureName(signature), "Low", context.Ancillary!) : TraitEncoder(context.Region);
    }

    // What a header's tag alone names, or null: a LAME tag but ffmpeg's "Lavf", a VBRI header, or
    // a LAME version string after the Xing header.
    private static (string Name, string What)? TagEncoder(HeaderFrame header)
    {
        VbrHeader vbr = header.Vbr;
        if (vbr.IsVbri)
        {
            return ("Fraunhofer", "VBRI header");
        }

        if (vbr.HasLameTag)
        {
            string text = vbr.LameEncoder.TrimEnd(' ');
            if (text.StartsWith("Lavf", StringComparison.Ordinal))
            {
                return null;
            }

            string name = text.StartsWith("Lavc", StringComparison.Ordinal) ? $"FFmpeg ({text})"
                : text.StartsWith("LAMEH", StringComparison.Ordinal) ? (text.Length > 5 ? $"Helix MP3 Encoder {text[5..]}" : "Helix MP3 Encoder")
                : LameRelease.Parse(text)?.Name ?? LameName(text) ?? text;
            return (name, "LAME tag");
        }

        return vbr.EncoderString is { } old && LameName(old) is { } oldName ? (oldName, "Version string") : null;
    }

    private static bool IsFfmpeg(string text) => text.StartsWith("Lavc", StringComparison.Ordinal) || text.StartsWith("Lavf", StringComparison.Ordinal);

    private static Node Evidence(Node at, string text) => new("Evidence", at.Offset, at.Length, text) { Description = Explain.Evidence };

    private static Node Evidence(Node from, Node to, string text) => new("Evidence", from.Offset, to.End - from.Offset, text) { Description = Explain.Evidence };

    private static Node EncoderNode(long offset, long length, string name) =>
        new("Encoder", offset, length, name) { Description = Explain.Encoder, Topic = Topics.EncoderDetection };

    private static Node Encoder(string name, string confidence, params Node[] evidence)
    {
        long start = evidence.Min(e => e.Offset);
        long end = evidence.Max(e => e.End);
        Node node = EncoderNode(start, end - start, name);
        node.Add("Confidence", start, end - start, confidence, Explain.Confidence);
        foreach (Node item in evidence)
        {
            node.Add(item);
        }

        return node;
    }

    private sealed class HeaderFrame(int index, Entry frame, VbrHeader vbr)
    {
        private List<Node>? _nodes;

        public int Index => index;

        public Entry Frame => frame;

        public VbrHeader Vbr => vbr;

        public List<Node> Nodes => _nodes ??= vbr.ToNodes(frame.Offset, 0, null);

        // The node that names the encoder: the VBRI header, the LAME tag or the version string, or
        // the Xing header.
        public Node TagNode => Nodes.FirstOrDefault(n => n.Name is "VBRI" or "LAME Tag" or "Encoder String") ?? Nodes.First(n => n.Name == "Xing");
    }

    // What the region's frames carry besides their headers: the LAME or Gogo signature in the
    // ancillary data, and mp3PRO, MP3 Surround or an OFL block.
    private sealed class EncoderContext
    {
        public EncoderContext(StreamState state, RegionState region, Signature? signature)
        {
            State = state;
            Region = region;
            Signature = signature;
            Ancillary = signature is { } found
                ? new Node("Evidence", found.Offset, found.Length, found.IsCut ? $"\"{found.Text}\" in ancillary data, cut off where the ancillary data ends" : $"\"{found.Text}\" in ancillary data")
                {
                    Description = Explain.Evidence,
                }
                : null;
            if (region.Enhancement is { } enhanced)
            {
                AncillaryEvidence.Add(Evidence(enhanced, $"{enhanced.Value} signature in the ancillary data of every frame"));
            }

            if (region.Ofl is { } ofl)
            {
                AncillaryEvidence.Add(Evidence(ofl, "Original File Length block"));
            }
        }

        public StreamState State { get; }

        public RegionState Region { get; }

        public Signature? Signature { get; }

        public Node? Ancillary { get; }

        public List<Node> AncillaryEvidence { get; } = [];

        public string? Enhancement => Region.Enhancement?.Value?.ToString();
    }

    // A version string in ancillary data; IsCut when it runs to the end of the data, where LAME may
    // have run out of bits to write it whole.
    private readonly record struct Signature(long Offset, int Length, string Text, bool IsCut);

    // A signature names Gogo, or LAME with its version unless that may be cut short.
    private static string SignatureName(Signature signature) =>
        signature.Text == "GOGO" ? "Gogo" : signature.IsCut ? "LAME" : LameRelease.Parse(signature.Text)?.Name ?? LameName(signature.Text)!;

    // "LAME3.88 (beta)" as "LAME 3.88 (beta)" where the version is not one LameRelease reads.
    private static string? LameName(string text)
    {
        text = text.TrimEnd(' ');
        if (LameRelease.Parse(text) is { } release)
        {
            return release.Name;
        }

        if (text.StartsWith("LAME", StringComparison.Ordinal))
        {
            return text.Length > 4 ? $"LAME {text[4..]}" : "LAME";
        }

        return text.Length >= 5 && text[0] == 'L' && char.IsAsciiDigit(text[1]) && text[2] == '.' ? $"LAME {text[1..]}" : null;
    }

    // The first "LAME" with a whole version in the ancillary data of the region's Layer III frames,
    // at any bit position: ancillary data starts where the main data ends, which need not be a byte
    // boundary, and runs on into the next frame's data when that frame's main data starts later, so
    // the pieces are searched joined. Without one, the longest version that runs to the end of the
    // data, then the first "LAME" alone, which LAME writes when fewer than 32 bits follow it.
    private static Signature? FindSignature(StreamState state, RegionState region)
    {
        ChunkedList<MainDataRun> runs = state.Build.Runs;
        ChunkedList<Entry> frames = state.Build.Frames;
        var ancillary = new AncillaryStream();
        for (int index = region.First; index < (region.End ?? frames.Count); index++)
        {
            state.CheckCancellation(index - region.First);
            Entry frame = frames[index];
            if (!frame.HasSideInfo)
            {
                continue;
            }

            long dataStart = frame.Offset + 4 + (frame.Crc >= 0 ? 2 : 0) + frame.DataLength;
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
                        ancillary.Search(final: true);
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

        ancillary.Search(final: true);
        return ancillary.Found ?? ancillary.Gogo ?? ancillary.Cut ?? ancillary.Plain;
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

        // The first "LAME" with a whole version, the longest with a version that may be cut, and
        // the first without one.
        public Signature? Found { get; private set; }

        public Signature? Cut { get; private set; }

        public Signature? Plain { get; private set; }

        public Signature? Gogo { get; private set; }

        public void Append(byte[] data, long offset, bool endsFrame)
        {
            _pieces.Add((_bytes.Count, offset));
            _bytes.AddRange(data);
            EndsFrame = endsFrame;
            if (_bytes.Count > SliceLength)
            {
                // A version at the slice's end is found whole once the next slice follows.
                Search(final: false);
                Keep(LameSignature.Length + 20);
            }
        }

        public void Clear()
        {
            _bytes.Clear();
            _pieces.Clear();
            EndsFrame = false;
        }

        // final: the data ends here, so a version that runs to its end may be cut short.
        public void Search(bool final)
        {
            if (_bytes.Count < LameSignature.Length)
            {
                return;
            }

            byte[] data = _bytes.ToArray();
            if (Gogo is null && SearchFill(data, GogoSignature) is { } gogo)
            {
                Gogo = Locate(gogo.Offset, gogo.Length, "GOGO", false);
            }

            if (Found is not null)
            {
                return;
            }

            (var whole, var cut, var plain) = MpegAudioParser.Search(data);
            if (whole is { } found)
            {
                Found = Locate(found.Offset, found.Length, found.Text, false);
            }

            if (final && cut is { } partial && (Cut is null || partial.Text.Length > Cut.Value.Text.Length))
            {
                Cut = Locate(partial.Offset, partial.Length, partial.Text, true);
            }

            if (plain is { } bare)
            {
                Plain ??= Locate(bare.Offset, bare.Length, bare.Text, false);
            }
        }

        private Signature Locate(long index, int length, string text, bool isCut)
        {
            long start = FileOffset((int)index);
            long end = FileOffset((int)index + length - 1) + 1;
            return new Signature(start, (int)(end - start), text, isCut);
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

    // In data, at any bit position: the first "LAME" with a version that ends before the data does,
    // the longest with a version that runs to the end of the data, and the first without a version.
    private static ((long Offset, int Length, string Text)? Whole, (long Offset, int Length, string Text)? Cut, (long Offset, int Length, string Text)? Plain) Search(byte[] data)
    {
        (long Offset, int Length, string Text)? cut = null;
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
                var found = ((long)start, (bits + 7) / 8, Encoding.ASCII.GetString(shifted, start, end - start));
                if (end - start > LameSignature.Length)
                {
                    if (end < shifted.Length)
                    {
                        return (found, cut, plain);
                    }

                    if (cut is null || found.Item3.Length > cut.Value.Text.Length)
                    {
                        cut = found;
                    }
                }
                else
                {
                    plain ??= found;
                }

                int next = shifted.AsSpan(start + 1).IndexOf(LameSignature);
                start = next < 0 ? -1 : start + 1 + next;
            }
        }

        return (null, cut, plain);
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

    // "an Info header", "a Xing header", "a VBRI header".
    private static string Article(string headerType) => headerType == "Info" ? "an" : "a";
}

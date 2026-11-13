using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The invariant the GUI's captions rest on (docs/design/spectral-analysis.md, "GUI"): a stream a parser reports through a codec route
// (ParseStream, ParsePackets) or as its own format, with the codec of a registered decoder, starts a decoding session in every analysis,
// so its Verification says NotRequested and the front end can offer to decode it. A stream without a Verification is read as "not decoded
// here", which is only true where its container does not hand it to its codec's parser (`*.codec-not-analyzed`).
public class DecoderSessionInvariantTests
{
    // The directories of the fixtures of each registered decoder's codec, and of the containers that may carry it.
    private static readonly string[] Directories = ["flac", "wavpack", "monkeys-audio", "truehd", "wav", "aiff", "matroska", "mp4", "ogg", "measurement", "tta", "alac", "mpeg", "ac3"];

    // The codec name each registered decoder's parser reports, by the decoder's ID.
    private static readonly Dictionary<string, string> DecoderOfCodec = new(StringComparer.Ordinal)
    {
        ["FLAC"] = "flac",
        ["WavPack lossless"] = "wavpack",
        ["WavPack hybrid"] = "wavpack",
        ["WavPack lossy"] = "wavpack",
        ["Monkey's Audio"] = "monkeys-audio",
        ["TTA"] = "tta",
        ["Apple Lossless"] = "alac",
        ["Dolby TrueHD"] = "truehd",
        ["MLP"] = "mlp",
        ["Vorbis"] = "vorbis",
        ["Opus"] = "opus",
        ["AC-3"] = "ac-3",
        ["MPEG-1 Layer I"] = "mpeg-audio",
        ["MPEG-1 Layer II"] = "mpeg-audio",
        ["MPEG-1 Layer III"] = "mpeg-audio",
        ["MPEG-2 Layer I"] = "mpeg-audio",
        ["MPEG-2 Layer II"] = "mpeg-audio",
        ["MPEG-2 Layer III"] = "mpeg-audio",
        ["MPEG-2.5 Layer I"] = "mpeg-audio",
        ["MPEG-2.5 Layer II"] = "mpeg-audio",
        ["MPEG-2.5 Layer III"] = "mpeg-audio",
        ["PCM"] = "pcm",
        ["PCM (floating-point)"] = "pcm",
        [CodecNames.ALaw] = "pcm",
        [CodecNames.MuLaw] = "pcm",
    };

    // Streams of these fixtures are reported as PCM and have no session, although no finding says a container did not hand them over: the
    // parser starts the `pcm` session only where `PcmCoding` can describe the stream (docs/design/spectral-analysis.md, "Data that is not PCM
    // stays out"), and these files declare a format or data it cannot: a block alignment that does not fit the format, a float of 16 bits,
    // a 14-byte format chunk, a list of wave data, a sample size of 0, a data chunk or sound chunk that is missing or has no frames, A-law with a sample size other than 8 bits or none.
    private const string NotDescribable = "PcmCoding cannot describe the stream";
    private const string CompandedWidth = "PcmCoding cannot describe the stream: A-law and mu-law have 8 bits, and the file states another width (wav.companded-bits) or none (a 14-byte format chunk)";

    // Excluded files with streams of both kinds: some have a session, the others have a header the decoder refuses.
    private static readonly HashSet<string> Mixed = new(StringComparer.Ordinal) { "ogg/opus-head-invalid.opus" };

    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        ["wav/built-alaw-16-bits.wav"] = CompandedWidth,
        ["wav/built-format-14-bytes-alaw.wav"] = CompandedWidth,
        ["wav/built-block-align-mismatch.wav"] = NotDescribable,
        ["wav/built-float-16-bits.wav"] = NotDescribable,
        ["wav/built-format-14-bytes-pcm.wav"] = NotDescribable,
        ["wav/built-wavl-list-without-fact.wav"] = NotDescribable,
        ["wav/built-wavl-list.wav"] = NotDescribable,
        ["wav/built-zero-bits.wav"] = NotDescribable,
        ["wav/external-iec61937-mp2-lsf-24000.wav"] = "MPEG audio in IEC 61937 bursts reaches the MPEG audio parser as packets, which it does not analyze (mpeg.packets-not-analyzed), so the stream is the container's own",
        ["matroska/encrypted-vorbis.mka"] = "the container hands over the headers and no audio packet (its samples are encrypted), and the Vorbis session starts at the first audio packet",
        ["matroska/vorbis-private-zlib-framing.mka"] = "a header libvorbis refuses (the framing bit of a header in the CodecPrivate), so the stream is parsed and not decoded",
        ["matroska/compression-bzlib.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/compression-hides-compression.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/compression-hides-encryption.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/compression-private-bzlib.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encoding-order.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-3des-aes-settings.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-3des.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-cbc.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-chain.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-empty.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-in-chain-middle.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-next-encoding.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-over-3des.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-over-algorithm-none.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-private.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-track-timestamp-scale.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted-under-bzlib.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encrypted.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/encryption-missing.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/same-order-aes-modes.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/same-order-encryption-and-stripping.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/same-order-over-encryption.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/same-order-stray-encryption.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/same-order-two-compressions.mka"] = "the container hands over no audio packet the Opus session can read (its frames are encrypted or compressed in a way the container does not undo), and the Opus session starts at the first audio packet",
        ["matroska/external-mkvmerge-zlib.mka"] = "the container hands over no audio frame the AC-3 session can read (its frames are compressed in a way the container does not undo), and the AC-3 session starts at the first frame",
        ["mp4/cenc-ac3.m4a"] = "the samples are encrypted, so the container hands over the header and no frame, and the AC-3 session starts at the first frame",
        ["mp4/ffmpeg-cenc-ac3.mp4"] = "the samples are encrypted, so the container hands over the header and no frame, and the AC-3 session starts at the first frame",
        ["ac3/bsid-9.ac3"] = "a bit stream ID of 9, which A/52 does not define: only its header is read, so the stream is parsed and not decoded",
        ["ac3/bsid-10.ac3"] = "a bit stream ID of 10, which A/52 does not define: only its header is read, so the stream is parsed and not decoded",
        ["ogg/opus-ambisonics-family-3.opus"] = "mapping family 3 (the demixing matrix), which the decoder does not apply, so the stream is parsed and not decoded",
        ["ogg/opus-head-invalid.opus"] = "an identification header the decoder refuses, so the stream is parsed and not decoded",
        ["mp4/ffmpeg-cenc-opus.mp4"] = "the samples are encrypted, so the container hands over the header and no audio packet, and the Opus session starts at the first audio packet",
        ["ogg/vorbis-header-layout.ogg"] = "a header libvorbis refuses (the comment header's framing bit), so the stream is parsed and not decoded",
        ["ogg/vorbis-ident-invalid.ogg"] = "an identification header libvorbis refuses, so the stream is parsed and not decoded",
        ["ogg/vorbis-setup-invalid.ogg"] = "a setup header libvorbis refuses, so the stream is parsed and not decoded",
        ["ogg/vorbis-setup-truncated.ogg"] = "a setup header cut short, which libvorbis refuses, so the stream is parsed and not decoded",
        ["alac/built-cookie-missing-mka.mka"] = "the track has no CodecPrivate, so the stream is the container's own and the codec parser reads no frame (alac.cookie-missing)",
        ["aiff/built-chunk-id-invalid.aiff"] = NotDescribable,
        ["aiff/built-chunk-limit.aiff"] = NotDescribable,
        ["aiff/built-no-frames.aiff"] = NotDescribable,
        ["aiff/built-no-sound-data.aiff"] = NotDescribable,
        ["aiff/built-sample-size-zero.aiff"] = NotDescribable,
    };

    [Fact]
    public void Every_stream_of_a_registered_decoders_codec_starts_a_session_unless_its_container_did_not_hand_it_over()
    {
        Analyzer analyzer = Analyzer.CreateDefault();
        HashSet<string> registered = [.. DefaultParsers.Decoders.Select(d => d.Id)];
        Assert.All(DecoderOfCodec.Values, id => Assert.Contains(id, registered));
        Assert.Equal(registered.Order(StringComparer.Ordinal), DecoderOfCodec.Values.Distinct().Order(StringComparer.Ordinal));

        var checkedStreams = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (string directory in Directories)
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, directory)).Order(StringComparer.Ordinal)
                .Where(p => Path.GetExtension(p) is not (".json" or ".md" or ".txt" or ".py")))
            {
                Report report = analyzer.AnalyzeFile(path);

                // A container states the finding once for each stream it does not hand over, at the track's sample entry, which no node of
                // the stream's own covers: the findings are a budget of streams that may lack a session, not a pass for the whole file.
                int notAnalyzed = report.Findings.Count(f => f.Code.EndsWith(".codec-not-analyzed", StringComparison.Ordinal)
                    || f.Code is "matroska.codec-private-missing" or "matroska.codec-private-invalid");
                var withoutSession = new List<string>();
                foreach (StreamInfo stream in report.Streams.Where(s => DecoderOfCodec.ContainsKey(s.Codec)))
                {
                    string id = DecoderOfCodec[stream.Codec];
                    string name = $"{directory}/{Path.GetFileName(path)}";
                    if (stream.Verification is { Decoding: DecodeStatus.NotRequested })
                    {
                        checkedStreams[id] = checkedStreams.GetValueOrDefault(id) + 1;
                    }
                    else if (!Excluded.ContainsKey(name))
                    {
                        withoutSession.Add($"{name}: a {stream.Codec} stream has {(stream.Verification is null ? "no verification" : stream.Verification.Decoding.ToString())}");
                    }
                }

                if (withoutSession.Count > notAnalyzed)
                {
                    failures.AddRange(withoutSession);
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // An excluded fixture that now has a session would hide a regression in the list: it must still be one the test skips.
        foreach (string name in Excluded.Keys.Where(n => !Mixed.Contains(n)))
        {
            Report report = analyzer.AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, name));
            Assert.DoesNotContain(report.Streams, s => DecoderOfCodec.ContainsKey(s.Codec) && s.Verification is not null);
        }

        // Every registered decoder was seen on streams of its own: the test would pass vacuously on a codec name that no stream has.
        foreach (string id in registered)
        {
            Assert.True(checkedStreams.GetValueOrDefault(id) > 0, $"No fixture stream of the decoder {id} was found.");
        }
    }

    // The fixtures the design names, whose streams have a decoder that the analysis never starts: a FLAC stream in MP4, which the container does not
    // hand to its codec's parser.
    [Theory]
    [InlineData("mp4/ffmpeg-cenc-flac.mp4", "FLAC")]
    public void A_stream_the_container_does_not_hand_over_has_no_verification_and_a_finding_says_why(string name, string codec)
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, name));
        StreamInfo stream = Assert.Single(report.Streams, s => s.Codec == codec);
        Assert.Null(stream.Verification);
        Assert.Contains(report.Findings, f => f.Code.EndsWith(".codec-not-analyzed", StringComparison.Ordinal));
    }

    // Matroska hands these codecs to their parsers: the stream has a session and no finding says it was not analyzed.
    [Theory]
    [InlineData("matroska/external-ffmpeg-flac.mka", "FLAC")]
    [InlineData("matroska/external-ffmpeg-truehd.mka", "Dolby TrueHD")]
    [InlineData("matroska/external-ffmpeg-wavpack.mka", "WavPack lossless")]
    [InlineData("matroska/external-ffmpeg-mlp-51.mka", "MLP")]
    public void A_stream_matroska_hands_over_has_a_session(string name, string codec)
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, name));
        StreamInfo stream = Assert.Single(report.Streams, s => s.Codec == codec);
        Assert.NotNull(stream.Verification);
        Assert.DoesNotContain(report.Findings, f => f.Code.EndsWith(".codec-not-analyzed", StringComparison.Ordinal));
    }
}

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
    private static readonly string[] Directories = ["flac", "wavpack", "monkeys-audio", "truehd", "wav", "aiff", "matroska", "mp4", "ogg", "measurement", "tta"];

    // The codec name each registered decoder's parser reports, by the decoder's ID.
    private static readonly Dictionary<string, string> DecoderOfCodec = new(StringComparer.Ordinal)
    {
        ["FLAC"] = "flac",
        ["WavPack lossless"] = "wavpack",
        ["WavPack hybrid"] = "wavpack",
        ["WavPack lossy"] = "wavpack",
        ["Monkey's Audio"] = "monkeys-audio",
        ["Dolby TrueHD"] = "truehd",
        ["MLP"] = "mlp",
        ["PCM"] = "pcm",
        ["PCM (floating-point)"] = "pcm",
    };

    // Streams of these fixtures are reported as PCM and have no session, although no finding says a container did not hand them over: the
    // parser starts the `pcm` session only where `PcmCoding` can describe the stream (docs/design/spectral-analysis.md, "Data that is not PCM
    // stays out"), and these files declare a format or data it cannot: a block alignment that does not fit the format, a float of 16 bits,
    // a 14-byte format chunk, a list of wave data, a sample size of 0, a data chunk or sound chunk that is missing or has no frames.
    private const string NotDescribable = "PcmCoding cannot describe the stream";

    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        ["wav/built-block-align-mismatch.wav"] = NotDescribable,
        ["wav/built-float-16-bits.wav"] = NotDescribable,
        ["wav/built-format-14-bytes-pcm.wav"] = NotDescribable,
        ["wav/built-wavl-list-without-fact.wav"] = NotDescribable,
        ["wav/built-wavl-list.wav"] = NotDescribable,
        ["wav/built-zero-bits.wav"] = NotDescribable,
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
                bool isNotAnalyzed = report.Findings.Any(f => f.Code.EndsWith(".codec-not-analyzed", StringComparison.Ordinal));
                foreach (StreamInfo stream in report.Streams.Where(s => DecoderOfCodec.ContainsKey(s.Codec)))
                {
                    string id = DecoderOfCodec[stream.Codec];
                    string name = $"{directory}/{Path.GetFileName(path)}";
                    if (stream.Verification is { Decoding: DecodeStatus.NotRequested })
                    {
                        checkedStreams[id] = checkedStreams.GetValueOrDefault(id) + 1;
                    }
                    else if (!isNotAnalyzed && !Excluded.ContainsKey(name))
                    {
                        failures.Add($"{name}: a {stream.Codec} stream has {(stream.Verification is null ? "no verification" : stream.Verification.Decoding.ToString())}");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // An excluded fixture that now has a session would hide a regression in the list: it must still be one the test skips.
        foreach (string name in Excluded.Keys)
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

    // The fixtures the design names, whose streams have a decoder that the analysis never starts: a FLAC, WavPack or TrueHD stream in
    // Matroska or MP4, which the container does not hand to its codec's parser.
    [Theory]
    [InlineData("matroska/external-ffmpeg-flac.mka", "FLAC")]
    [InlineData("matroska/external-ffmpeg-truehd.mka", "Dolby TrueHD")]
    [InlineData("matroska/external-ffmpeg-wavpack.mka", "WavPack")]
    [InlineData("mp4/ffmpeg-cenc-flac.mp4", "FLAC")]
    public void A_stream_the_container_does_not_hand_over_has_no_verification_and_a_finding_says_why(string name, string codec)
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, name));
        StreamInfo stream = Assert.Single(report.Streams, s => s.Codec == codec);
        Assert.Null(stream.Verification);
        Assert.Contains(report.Findings, f => f.Code.EndsWith(".codec-not-analyzed", StringComparison.Ordinal));
    }
}

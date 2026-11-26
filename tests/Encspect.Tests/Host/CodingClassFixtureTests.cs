using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Aiff;
using Encspect.Formats.Matroska;
using Encspect.Formats.Mp4;
using Encspect.Host;

namespace Encspect.Tests.Host;

// Every coding name the code can write has a class or is listed as one without, so a new codec needs a decision; every golden stream holds the class its
// parser states, and the verification's IsLossy and isCorrected agree with it.
public partial class CodingClassFixtureTests
{
    // Names whose class the stream or the name alone does not settle, or that are no coding of audio.
    private static readonly string[] NamesWithoutAClass =
    [
        "Other", "Video", "Encrypted audio", "Encrypted video", "RealAudio", "WavPack", "MPEG-4 audio", "SLS", "SLS non-core", "DTS-HD", "MAT",
        "Sonarc", "MSN Audio", "Control Resources VQLPC", "Control Resources CR10", "Echo Speech EchoSC1", "Audiofile AF36", "Audiofile AF10",
        "DSP Solutions DIGISTD", "DSP Solutions DIGIFIX", "DSP Solutions DIGIREAL", "DRM", "Raw SPORT", "unknown", "extensible", "null", "reserved", "General MIDI",
        "Algorithmic synthesis and audio effects", "Audio Sync", "SMR Simple", "SMR Main", "PS", "SBR", "TTSI", "Main synthetic", "Wavetable synthesis", "Microsoft ACM audio", "QuickTime audio", "other", "iTunes protected audio", "iTunes protected video",
        "WavPack hybrid", "WavPack lossy", "WavPack lossless", "WavPack correction data", "OptimFROG", "OptimFROG correction data",
    ];

    private static readonly string[] MatroskaCodecIds = [.. Regex.Matches(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "..", "..", "src", "Encspect.Formats.Matroska", "Codecs.cs")), "\"([AVS]_[^\"]*)\"").Select(m => m.Groups[1].Value).Distinct()];

    private static IEnumerable<string> CodingNames()
    {
        for (int tag = 0; tag <= 0xFFFF; tag++)
        {
            if (WaveFormatNames.NameOf(tag) is { } name)
            {
                yield return name;
            }
        }

        var all = (IEnumerable<AifcType>)typeof(AifcType).GetField("All", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (AifcType type in all)
        {
            yield return type.Codec;
        }

        for (int type = 0; type <= 46; type++)
        {
            yield return AacTables.ObjectTypeName(type);
        }

        foreach (string id in MatroskaCodecIds)
        {
            if (Codecs.NameOf(id) is { } name)
            {
                yield return name;
            }
        }

        foreach (string format in new[] { ".mp3", "alac", "fLaC", "mlpa", "ipcm", "fpcm", "Opus", "ac-3", "ec-3", "ac-4", "dtsc", "dtsh", "dtsl", "dtse", "lpcm", "twos", "sowt", "in24", "in32", "fl32", "fl64", "raw ", "alaw", "ulaw", "samr", "sawb", "enca", "encv", "drms", "drmi", "ima4", "sevc", "sqcp", "QDM2", "mp4a" })
        {
            yield return Mp4Parser.StreamCodecName(format, null);
        }

        // The names the parsers build from their own figures: AAC's composites, MPEG audio's version and layer, Musepack and Speex.
        foreach (string core in Enumerable.Range(0, 47).Select(AacTables.ObjectTypeName).Where(c => c != "DST" && (CodingClasses.OfName(c) is not null || c.StartsWith("SLS", StringComparison.Ordinal))).Distinct())
        {
            yield return $"{core} with SBR";
            yield return $"{core} with SBR and PS";
            yield return $"{core} (no SBR signaled explicitly)";
        }

        foreach (string name in new[] { "HE-AAC", "HE-AAC v2", "HE-AAC (no PS signaled explicitly)", "xHE-AAC (USAC)", "ER AAC ELD with SBR" })
        {
            yield return name;
        }

        foreach (int type in Enumerable.Range(0, 256))
        {
            yield return Mp4Parser.StreamCodecName("mp4a", type);
        }

        foreach (string version in new[] { "MPEG-1", "MPEG-2", "MPEG-2.5" })
        {
            foreach (string layer in new[] { "I", "II", "III" })
            {
                yield return $"{version} Layer {layer}";
            }
        }

        foreach (string name in new[] { "Musepack SV4", "Musepack SV5", "Musepack SV6", "Musepack SV7", "Musepack SV8", "Speex", "Speex narrowband", "Speex wideband", "Speex ultra-wideband",
            "Vorbis", "Opus", "DTS", "DTS-ES", "DTS 96/24", "DTS Express", "DTS-HD High Resolution Audio", "DTS-HD Master Audio", "DTS-HD Master Audio + DTS:X", "DTS-HD Master Audio + DTS:X IMAX",
            "Windows Media Audio 1", "Windows Media Audio 2", "Windows Media Audio Professional", "Windows Media Audio Voice", "Windows Media Audio Lossless", "Dolby TrueHD", "MLP", "E-AC-3", "AC-3" })
        {
            yield return name;
        }

        foreach (FieldInfo field in typeof(CodecNames).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            yield return (string)field.GetRawConstantValue()!;
        }

        var burst = typeof(Encspect.Formats.Wav.WavParser).Assembly.GetType("Encspect.Formats.Wav.Iec61937")!.GetField("Types", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (object entry in ((System.Collections.IDictionary)burst).Values)
        {
            yield return (string)entry.GetType().GetProperty("Name")!.GetValue(entry)!;
        }
    }

    [Fact]
    public void EveryCodingNameTheCodeCanWriteHasAClassOrIsListedWithoutOne()
    {
        string[] names = [.. CodingNames().Distinct()];
        Assert.True(names.Length > 100);

        string[] missing = [.. names.Where(n => CodingClasses.OfName(n) is null && !NamesWithoutAClass.Contains(n) && !n.StartsWith("SLS", StringComparison.Ordinal))];
        Assert.True(missing.Length == 0, $"These coding names have no class and are not listed as ones that have none: {string.Join(", ", missing.Select(n => $"\"{n}\""))}.");

        foreach (string name in NamesWithoutAClass.Where(n => n is not ("WavPack hybrid" or "WavPack lossy" or "WavPack lossless" or "OptimFROG" or "OptimFROG correction data" or "WavPack correction data")))
        {
            Assert.Null(CodingClasses.OfName(name));
        }
    }

    [Theory]
    [InlineData("A-law", CodingClass.Lossy)]
    [InlineData("WMA Professional type I", CodingClass.Lossy)]
    [InlineData("ESS Technology AC-3", CodingClass.Lossy)]
    [InlineData("AC-4", CodingClass.Lossy)]
    [InlineData("AMR", CodingClass.Lossy)]
    [InlineData("TwinVQ", CodingClass.Lossy)]
    [InlineData("CELP", CodingClass.Lossy)]
    [InlineData("HVXC", CodingClass.Lossy)]
    [InlineData("ER TwinVQ", CodingClass.Lossy)]
    [InlineData("LD MPEG Surround", CodingClass.Lossy)]
    [InlineData("ALS", CodingClass.Lossless)]
    public void TheClassesTheReviewNamed(string name, CodingClass expected) => Assert.Equal(expected, CodingClasses.OfName(name));

    [Theory]
    [InlineData("SLS with SBR")]
    [InlineData("SLS (no SBR signaled explicitly)")]
    public void ASlsCompositeKeepsTheNoClassOfSls(string name) => Assert.Null(CodingClasses.OfName(name));

    [Fact]
    public void AMp4DtsHdTrackNoParserReadsHasNoClass() => Assert.Null(CodingClasses.OfName(Mp4Parser.StreamCodecName("dtsh", null)));

    private static IEnumerable<JsonElement> Streams()
    {
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*.report.json", SearchOption.AllDirectories))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 4096 });
            JsonElement root = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement[0] : document.RootElement;
            if (root.TryGetProperty("streams", out JsonElement streams))
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    yield return stream.Clone();
                }
            }
        }
    }

    private static string? Word(JsonElement stream) => stream.TryGetProperty("codingClass", out JsonElement value) ? value.GetString() : null;

    private static bool IsCorrectionFile(JsonElement stream) => stream.TryGetProperty("isCorrectionFile", out JsonElement c) && c.GetBoolean();

    [Fact]
    public void EveryStreamOfEveryGoldenHoldsTheClassItsParserOrNameGives()
    {
        int checkedStreams = 0;
        foreach (JsonElement stream in Streams())
        {
            string codec = stream.GetProperty("codec").GetString()!;
            string? word = Word(stream);
            bool isAudio = !stream.TryGetProperty("kind", out JsonElement kind) || kind.GetString() == "Audio";

            if (word is null)
            {
                Assert.True(!isAudio || IsCorrectionFile(stream) || NamesWithoutAClass.Contains(codec), $"A stream of \"{codec}\" has no class and its name is not listed as one that has none.");
            }
            else
            {
                Assert.Contains(word, new[] { "lossless", "lossy", "hybrid" });
                if (codec.StartsWith("WavPack", StringComparison.Ordinal) || codec == "OptimFROG")
                {
                    // The parsers state these: a hybrid stream is "WavPack hybrid" or an OptimFROG file with the DualStream claim.
                    Assert.Equal(codec == "WavPack hybrid" || (codec == "OptimFROG" && stream.TryGetProperty("claims", out _)), word == "hybrid");
                }
                else
                {
                    Assert.Equal(word, CodingText.Word(CodingClasses.OfName(codec)));
                }
            }

            checkedStreams++;
        }

        Assert.True(checkedStreams > 1000);
    }

    [Fact]
    public void LegacyWavPackLossyModeIsLossyAndHasNoCorrectionFile()
    {
        int found = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "wavpack", "legacy"), "*.report.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 4096 });
            foreach (JsonElement stream in document.RootElement[0].GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec").GetString() == "WavPack lossy"))
            {
                Assert.Equal("lossy", Word(stream));
                found++;
            }
        }

        Assert.True(found >= 10);
    }

    // IsLossy and isCorrected agree with the class: lossy output only from a lossy or a hybrid coding, never where a consistent correction file is applied, and a
    // correction file only for a hybrid coding.
    [Fact]
    public void TheVerificationsIsLossyAndIsCorrectedAgreeWithTheClass()
    {
        int lossy = 0;
        foreach (JsonElement stream in Streams().Where(s => !IsCorrectionFile(s)))
        {
            if (!stream.TryGetProperty("verification", out JsonElement verification))
            {
                continue;
            }

            string? word = Word(stream);
            bool isLossy = verification.TryGetProperty("isLossy", out JsonElement l) && l.GetBoolean();
            bool isCorrected = verification.TryGetProperty("isCorrected", out JsonElement w) && w.GetBoolean();
            string codec = stream.GetProperty("codec").GetString()!;

            if (isLossy)
            {
                Assert.True(word is "lossy" or "hybrid" || word is null && codec == "WavPack lossy", $"A stream of \"{codec}\" is verified as lossy output and has the class {word ?? "none"}.");
                Assert.False(isCorrected, $"A stream of \"{codec}\" is verified as lossy output and with its correction file.");
                lossy++;
            }

            if (isCorrected)
            {
                Assert.Equal("hybrid", word);
            }

            if (word is "lossless")
            {
                Assert.False(isLossy);
            }
        }

        Assert.True(lossy > 0);
    }
}

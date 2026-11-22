using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Every fixture's streams hold a class that follows from the coding, the codings without one are the ones named here, and the verification's IsLossy agrees with it.
public class CodingClassFixtureTests
{
    private static readonly string[] CodecsWithoutAClass =
    [
        "Other", "Video", "Encrypted audio", "Encrypted video", "RealAudio", "WavPack", "MPEG-4 audio", "SLS", "SLS non-core",
        "WavPack correction data", "OptimFROG correction data",
    ];

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

    [Fact]
    public void EveryStreamOfEveryGoldenHasTheClassOfItsCodingOrIsListedWithout()
    {
        int checkedStreams = 0;
        foreach (JsonElement stream in Streams())
        {
            string codec = stream.GetProperty("codec").GetString()!;
            string? word = stream.TryGetProperty("codingClass", out JsonElement value) ? value.GetString() : null;
            bool isAudio = !stream.TryGetProperty("kind", out JsonElement kind) || kind.GetString() == "Audio";
            bool isCorrectionFile = stream.TryGetProperty("isCorrectionFile", out JsonElement c) && c.GetBoolean();

            if (word is null)
            {
                Assert.True(!isAudio || isCorrectionFile || CodecsWithoutAClass.Contains(codec), $"The codec \"{codec}\" has no class and is not listed as one that has none.");
            }
            else
            {
                Assert.Contains(word, new[] { "lossless", "lossy", "hybrid" });
                if (codec != "OptimFROG")
                {
                    Assert.Equal(word, CodingText.Word(CodingClasses.OfName(codec)));
                }
            }

            checkedStreams++;
        }

        Assert.True(checkedStreams > 1000);
    }

    [Fact]
    public void AStreamVerifiedAsLossyOutputHasALossyOrHybridCodingClass()
    {
        int lossy = 0;
        foreach (JsonElement stream in Streams().Where(s => !s.TryGetProperty("isCorrectionFile", out JsonElement c) || !c.GetBoolean()))
        {
            if (stream.TryGetProperty("verification", out JsonElement verification) && verification.TryGetProperty("isLossy", out JsonElement isLossy) && isLossy.GetBoolean())
            {
                string? word = stream.TryGetProperty("codingClass", out JsonElement value) ? value.GetString() : null;
                Assert.True(word is "lossy" or "hybrid", $"A stream of \"{stream.GetProperty("codec").GetString()}\" is verified as lossy output and has the class {word ?? "none"}.");
                lossy++;
            }
        }

        Assert.True(lossy > 0);
    }
}

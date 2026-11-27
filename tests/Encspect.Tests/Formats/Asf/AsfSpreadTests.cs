using System.Text.Json;
using Encspect.Formats.Asf;

namespace Encspect.Tests.Formats.Asf;

// When spread audio counts as scrambled (docs/formats/asf-wma.md). The `spread-*` fixtures hold the cases around the rule's boundaries; the ASF cross-check
// (crosscheck_asf_fixtures.py, check_spread) pins what ffmpeg does with each by its decoded bytes, "descrambled" or "as is", and the rule agrees with it.
public class AsfSpreadTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (string path in Directory.GetFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), "spread-*.expected.json").Order(StringComparer.Ordinal))
            {
                names.Add(Path.GetFileName(path).Replace(".expected.json", "", StringComparison.Ordinal));
            }

            return names;
        }
    }

    [Fact]
    public void BoundaryFixturesExist() => Assert.True(Names.Count >= 9);

    [Theory]
    [MemberData(nameof(Names))]
    public void RuleAgreesWithFfmpeg(string name)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "asf", name + ".expected.json")));
        JsonElement crosscheck = manifest.RootElement.GetProperty("crosscheck");
        if (!crosscheck.TryGetProperty("spread_audio", out JsonElement spread))
        {
            return;
        }

        var audio = new SpreadAudio(spread.GetProperty("span").GetByte(), spread.GetProperty("virtual_packet_length").GetUInt16(), spread.GetProperty("virtual_chunk_length").GetUInt16(), 1);
        Assert.Equal(crosscheck.GetProperty("spread").GetString() == "descrambled", audio.Scrambles);
    }
}

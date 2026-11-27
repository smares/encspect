using System.Text.Json;
using Encspect.Core;
using Encspect.Formats.Asf;
using Encspect.Host;

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
                if (path.EndsWith("spread-audio.expected.json", StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(Path.GetFileName(path).Replace(".expected.json", "", StringComparison.Ordinal));
            }

            return names;
        }
    }

    [Fact]
    public void BoundaryFixturesExist() => Assert.Equal(12, Names.Count);

    [Theory]
    [MemberData(nameof(Names))]
    public void RuleAgreesWithFfmpeg(string name)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "asf", name + ".expected.json")));
        JsonElement crosscheck = manifest.RootElement.GetProperty("crosscheck");
        JsonElement spread = crosscheck.GetProperty("spread_audio");
        byte span = spread.GetProperty("span").GetByte();
        ushort packet = spread.GetProperty("virtual_packet_length").GetUInt16();
        ushort chunk = spread.GetProperty("virtual_chunk_length").GetUInt16();
        Assert.Equal(crosscheck.GetProperty("rule_scrambles").GetBoolean(), new SpreadAudio(span, packet, chunk, 1).Scrambles);

        // The fields the rule reads, as the analysis reads them from the file.
        Report report = Analyzer.CreateDefault().Analyze(name + ".asf", File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "asf", name + ".asf")));
        Assert.Equal(span, Value(report.Root, "Span"));
        Assert.Equal(packet, Value(report.Root, "Virtual Packet Length"));
        Assert.Equal(chunk, Value(report.Root, "Virtual Chunk Length"));
    }

    private static long? Value(Node node, string name) => node.Name == name ? node.Value?.AsInteger() : node.Children.Select(c => Value(c, name)).FirstOrDefault(v => v is not null);
}

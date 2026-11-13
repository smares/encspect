using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A mantissa code that its quantizer has no value for (7 of the 7-level quantizer, 15 of the 15-level one: Tables 7.21 and 7.23) is a finding of its own, worded without the
// numbered checks of A/52 7.10.2, which do not list it.
public class Ac3MantissaFindingTests
{
    [Fact]
    public void A_code_without_a_value_is_reported_with_its_table_and_not_as_a_numbered_check()
    {
        const string name = "ac3/ac3-signal-unequal-bandwidth.ac3";
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        foreach (Node mantissas in Find(Fixtures.AnalyzeMpeg(name).Root, "Mantissas"))
        {
            long start = (mantissas.Offset * 8) + (mantissas.BitOffset ?? 0);
            long bits = mantissas.Value?.AsInteger() ?? 0;
            for (long bit = start; bit < start + bits; bit++)
            {
                data[bit >> 3] |= (byte)(0x80 >> (int)(bit & 7));
            }
        }

        Report report = Analyzer.CreateDefault().Analyze("mantissas.ac3", new MemoryStream(data));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "ac3.mantissa-code-invalid");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("Table 7.21", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("7.10.2", finding.Message, StringComparison.Ordinal);
    }

    // Coupling in use with no channel in it (check 2) counts the coupling channel's mantissa bits in the block's Mantissas field but has no channel to read them after, so the codes read
    // stop short of the field's end; the findings about the codes name the field, as the tree shows it (found by the fuzzer, seed 120138190, iteration 67402).
    [Fact]
    public void A_finding_about_mantissas_names_the_field_even_where_the_codes_read_stop_short_of_it()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "ac3-mantissa-finding-with-no-coupled-channel.bin"));

        Report report = Analyzer.CreateDefault().Analyze("fuzz.ac3", new MemoryStream(data));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "ac3.mantissa-code-invalid");
        Assert.NotNull(finding.Path);
        Assert.Equal("Mantissas", finding.Node!.Name);
        Assert.Equal((0x3670L, 4, 1419), (finding.Node.Offset, finding.Node.BitOffset, finding.Node.BitLength));
        Assert.Contains(report.Findings, f => f.Code == "ac3.block-inconsistent" && f.Message.Contains("check 2", StringComparison.Ordinal));
    }

    private static IEnumerable<Node> Find(Node node, string name)
    {
        if (node.Name == name)
        {
            yield return node;
        }

        foreach (Node child in node.Children)
        {
            foreach (Node found in Find(child, name))
            {
                yield return found;
            }
        }
    }
}

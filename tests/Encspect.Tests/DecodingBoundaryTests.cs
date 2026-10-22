using System.Reflection;
using Encspect.Core;
using Encspect.Host;
using Encspect.Tests.BoundarySamples;

namespace Encspect.Tests;

// Parser code never references the Decoding namespace of its own project (docs/design/decoders.md, "How a test enforces
// it"), checked in the compiled assemblies of the format and codec parsers the host lists.
public class DecodingBoundaryTests
{
    private static IEnumerable<Assembly> ParserAssemblies() =>
        DefaultParsers.Formats.Cast<object>().Concat(DefaultParsers.Codecs).Select(p => p.GetType().Assembly).Distinct();

    private static string Describe(BoundaryViolation v) =>
        v.IsInversion ? $"{v.From} ({v.Member}) extends {v.To}, which is not decoding code" : $"{v.From}, {v.Member}, reaches {v.To}";

    [Fact]
    public void NoParserCodeOfTheFormatAssembliesReachesItsDecodingNamespace()
    {
        List<Assembly> assemblies = ParserAssemblies().ToList();
        Assert.True(assemblies.Count >= 10);

        var problems = new List<string>();
        foreach (Assembly assembly in assemblies)
        {
            problems.AddRange(BoundaryScanner.Scan(assembly).Select(v => $"{assembly.GetName().Name}: {Describe(v)}"));
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void ADecodingTypeExtendsOnlyContractsAndFrameworkTypesNeverTheTypesOfItsParser()
    {
        // The inversion: a parser could call into a decoder through an abstract type or an interface of its own project.
        foreach (Assembly assembly in ParserAssemblies())
        {
            Assert.DoesNotContain(BoundaryScanner.Scan(assembly), v => v.IsInversion);
        }
    }

    [Fact]
    public void TheNegativeControlReportsExactlyItsPairs()
    {
        const string Namespace = "Encspect.Tests.BoundarySamples.";
        IReadOnlyList<BoundaryViolation> found = BoundaryScanner.Scan(typeof(DecodingBoundaryTests).Assembly);

        // Only the control's own types: the other tests of this assembly use the decoders of the format projects on purpose,
        // and those are definitions of other assemblies, which the scan does not count.
        HashSet<string> actual = found.Select(v => $"{v.From}|{v.To}").ToHashSet();
        HashSet<string> expected = ControlPairs.Expected.Select(p => $"{Namespace}{p.From}|{Namespace}{p.To}").ToHashSet();

        Assert.Equal([.. expected.Except(actual).Order()], []);
        Assert.Equal([.. actual.Except(expected).Order()], []);
        Assert.Equal(ControlPairs.Expected.Length, expected.Count);
    }

    [Fact]
    public void TheControlHasOneSampleForEachInversionAndForEachPathOfAParserType()
    {
        IReadOnlyList<BoundaryViolation> found = BoundaryScanner.Scan(typeof(DecodingBoundaryTests).Assembly);

        Assert.Equal(2, found.Count(v => v.IsInversion));
        Assert.Equal(ControlPairs.Expected.Length - 2, found.Where(v => !v.IsInversion).Select(v => v.From).Distinct().Count());
    }

    [Theory]
    [InlineData("Encspect.Formats.Flac.Decoding", true)]
    [InlineData("Encspect.Formats.Flac.Decoding.Tables", true)]
    [InlineData("Decoding", true)]
    [InlineData("Encspect.Formats.Flac.DecodingTables", false)]
    [InlineData("Encspect.Formats.Flac", false)]
    [InlineData("Encspect.Formats.Flac.Decoder", false)]
    [InlineData("", false)]
    public void ANamespaceIsDecodingCodeWhereDecodingIsAWholeSegment(string ns, bool expected) =>
        Assert.Equal(expected, BoundaryScanner.IsDecodingNamespace(ns));
}

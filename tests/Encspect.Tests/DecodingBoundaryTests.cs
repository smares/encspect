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

        Assert.True(expected.Except(actual).Count() == 0, "Pairs the scan missed: " + string.Join("; ", expected.Except(actual).Order()));
        Assert.True(actual.Except(expected).Count() == 0, "Pairs the scan added: " + string.Join("; ", actual.Except(expected).Order()));
        Assert.Equal(ControlPairs.Expected.Length, expected.Count);
    }

    [Fact]
    public void TheControlHasOneSampleForEachInversionAndForEachPathOfAParserType()
    {
        IReadOnlyList<BoundaryViolation> found = BoundaryScanner.Scan(typeof(DecodingBoundaryTests).Assembly);

        Assert.Equal(2, found.Count(v => v.IsInversion));
        Assert.Equal(ControlPairs.Expected.Length - 2, found.Where(v => !v.IsInversion).Select(v => v.From).Distinct().Count());
    }

    public static TheoryData<string> SyntheticNames()
    {
        var names = new TheoryData<string>();
        foreach (BoundarySynthetic.Case scenario in BoundarySynthetic.Cases)
        {
            names.Add(scenario.Name);
        }

        return names;
    }

    // In each small assembly one way of reaching Syn.Decoding.T is the only evidence of the pair: it is found, and it is lost
    // exactly when that way is switched off.
    [Theory]
    [MemberData(nameof(SyntheticNames))]
    public void EachPathIsNeededToFindItsPair(string name)
    {
        BoundarySynthetic.Case scenario = BoundarySynthetic.Cases.Single(c => c.Name == name);
        byte[] image = BoundarySynthetic.Build(scenario);

        BoundaryViolation found = Assert.Single(BoundaryScanner.Scan(new MemoryStream(image)));
        Assert.Equal((scenario.From, scenario.To, false), (found.From, found.To, found.IsInversion));

        Assert.Empty(BoundaryScanner.Scan(new MemoryStream(image), scenario.Path));
    }

    [Fact]
    public void EveryPathHasACaseThatNeedsIt()
    {
        BoundaryScanner.ScanPath all = Enum.GetValues<BoundaryScanner.ScanPath>().Aggregate(BoundaryScanner.ScanPath.None, (a, b) => a | b);
        BoundaryScanner.ScanPath covered = BoundarySynthetic.Cases.Aggregate(BoundaryScanner.ScanPath.None, (a, c) => a | c.Path);

        Assert.Equal(all, covered);
    }

    [Fact]
    public void ASerializedNameOfAnotherAssemblyIsNotOurType()
    {
        var builder = new BoundarySynthetic.Builder();
        builder.Host().SetCustomAttribute(typeof(System.ComponentModel.TypeConverterAttribute).GetConstructor([typeof(Type)])!, BoundarySynthetic.TypeofBlob("Syn.Decoding.T, Other, Version=1.0.0.0"));

        Assert.Empty(BoundaryScanner.Scan(new MemoryStream(builder.Save())));
    }

    [Theory]
    [InlineData(new byte[] { 0x24 }, "is no opcode")]
    [InlineData(new byte[] { 0xFE }, "two-byte opcode")]
    [InlineData(new byte[] { 0xFE, 0xFF }, "is no opcode")]
    [InlineData(new byte[] { 0x28, 0x01 }, "past the end")]
    [InlineData(new byte[] { 0x45, 0xFF, 0xFF, 0xFF, 0x7F }, "switch targets")]
    [InlineData(new byte[] { 0x21, 1, 2, 3, 4, 5, 6, 7, 8, 0x28, 0, 0 }, "past the end")]
    public void AWrongOperandSizeFailsTheWalkNamingTheMethodAndTheOffset(byte[] il, string why)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => BoundaryScanner.TokensIn(il, "Run"));

        Assert.Contains("in Run at IL offset", ex.Message);
        Assert.Contains(why, ex.Message);
    }

    [Fact]
    public void TheWalkReadsTheTokensOfABodyWithEveryKindOfOperandAroundThem()
    {
        byte[] il =
        [
            0x1F, 0x05,                         // ldc.i4.s: one byte
            0x20, 1, 2, 3, 4,                   // ldc.i4: four
            0x21, 1, 2, 3, 4, 5, 6, 7, 8,       // ldc.i8: eight
            0x23, 1, 2, 3, 4, 5, 6, 7, 8,       // ldc.r8: eight
            0x22, 1, 2, 3, 4,                   // ldc.r4: four
            0xFE, 0x09, 0x01, 0x00,             // ldarg: a two-byte opcode with a two-byte operand
            0x2B, 0x00,                         // br.s: one
            0x45, 0x02, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, // switch with two targets
            0x28, 0x01, 0x00, 0x00, 0x06,       // call MethodDef 1
            0x7B, 0x02, 0x00, 0x00, 0x04,       // ldfld Field 2
            0xFE, 0x15, 0x03, 0x00, 0x00, 0x01, // initobj TypeRef 3
            0xFE, 0x06, 0x04, 0x00, 0x00, 0x06, // ldftn MethodDef 4
            0x2A,                               // ret
        ];

        List<System.Reflection.Metadata.EntityHandle> tokens = BoundaryScanner.TokensIn(il, "Run");

        Assert.Equal([0x06000001, 0x04000002, 0x01000003, 0x06000004], tokens.Select(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken));
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

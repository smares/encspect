using Encspect.Core;
using Encspect.Host;
using Builder = Encspect.Tests.OptimFrogBuilder;

namespace Encspect.Tests;

// The class the OptimFROG parser states: from its evidence about the DualStream and nothing else.
public class OptimFrogCodingClassTests
{
    private static CodingClass? ClassOf(byte[] data) => Analyzer.CreateDefault().Analyze("x.ofr", new MemoryStream(data)).Streams.Single().CodingClass;

    [Fact]
    public void TheDualStreamClaimMakesTheCodingHybrid() =>
        Assert.Equal(CodingClass.Hybrid, ClassOf(Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Comp(88200, reserved: 0x1101), Builder.Tail())));

    [Fact]
    public void DataBlocksOfALosslessFileMakeItLossless() =>
        Assert.Equal(CodingClass.Lossless, ClassOf(Builder.Standard()));

    [Fact]
    public void AMixtureOfBlocksSettlesNothing() =>
        Assert.Null(ClassOf(Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Comp(44100, reserved: 0x1101), Builder.Comp(44100, reserved: 0x1042), Builder.Tail())));

    [Fact]
    public void AFileWithoutAWholeDataBlockSettlesNothing() =>
        Assert.Null(ClassOf(Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Tail())));
}

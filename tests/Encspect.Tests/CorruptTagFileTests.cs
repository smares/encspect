namespace Encspect.Tests;

// Damaged variants of every tag fixture (see CorruptFiles), and random bytes. The MPEG, MP4, Ogg and DTS
// fixtures have classes of their own below so that the test runner takes them in parallel: one theory's rows
// run one after the other, and 760 fixtures with 1,300 variants each took most of a test run in one class.
public class CorruptTagFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("tags/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);

    [Fact]
    public void RandomFilesProduceReports() => CorruptFiles.RandomFilesProduceReports();
}

// The fixtures in tests/fixtures/mpeg.
public class CorruptMpegFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/layer2, layer3, vbr and stats.
public class CorruptLayerFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("layer", StringComparison.Ordinal) || n.StartsWith("vbr/", StringComparison.Ordinal) || n.StartsWith("stats/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/mp4, adts and aac.
public class CorruptMp4FileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mp4/", StringComparison.Ordinal) || n.StartsWith("adts/", StringComparison.Ordinal) || n.StartsWith("aac/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/ogg.
public class CorruptOggFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("ogg/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/dts.
public class CorruptDtsFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("dts/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

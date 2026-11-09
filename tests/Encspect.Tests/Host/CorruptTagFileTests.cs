namespace Encspect.Tests;

// Damaged variants of every tag fixture (see CorruptFiles), and random bytes. The other fixtures have classes
// of their own below so that the test runner takes them in parallel and the CI shards can divide them: one
// theory's rows run one after the other, and 760 fixtures with 1,300 variants each took most of a test run
// in one class.
public class CorruptTagFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("tags/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);

    [Fact]
    public void RandomFilesProduceReports() => CorruptFiles.RandomFilesProduceReports();
}

// The fixtures in tests/fixtures/mpeg that LAME wrote.
public class CorruptLameFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg/external-lame-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The other fixtures in tests/fixtures/mpeg.
public class CorruptMpegFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg/", StringComparison.Ordinal) && n.IndexOf('/', 5) < 0 && !n.StartsWith("mpeg/external-lame-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/mpeg/layer2 and mpeg/layer3.
public class CorruptLayerFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg/layer2/", StringComparison.Ordinal) || n.StartsWith("mpeg/layer3/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/mpeg/vbr and mpeg/stats.
public class CorruptVbrFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg/vbr/", StringComparison.Ordinal) || n.StartsWith("mpeg/stats/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/mp4.
public class CorruptMp4FileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mp4/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/aac, with aac/adts.
public class CorruptAdtsFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("aac/", StringComparison.Ordinal));

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

// The fixtures in tests/fixtures/ac3, among them the EMDF containers and object audio payloads.
public class CorruptAc3FileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("ac3/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/matroska.
public class CorruptMatroskaFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("matroska/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/musepack.
public class CorruptMusepackFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("musepack/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The FLAC fixtures written by flac and FFmpeg, each analyzed with and without decoding.
public class CorruptFlacFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("flac/external-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The OptimFROG fixtures: the files ofr, off and ofs wrote (the correction files among them) and the hand-built ones.
public class CorruptOptimFrogFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("optimfrog/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The TAK fixtures: the files Takc wrote and the hand-built ones.
public class CorruptTakFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("tak/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The TTA fixtures written by FFmpeg and libtta-c.
public class CorruptTtaFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("tta/external-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built TTA fixtures.
public class CorruptTtaBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("tta/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The ALAC fixtures written by FFmpeg, Apple's alacconvert, mkvmerge and afconvert, and the CAF files of afconvert (which no parser reads).
public class CorruptAlacFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("alac/", StringComparison.Ordinal) && !n.StartsWith("alac/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built ALAC fixtures.
public class CorruptAlacBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("alac/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built FLAC fixtures, each analyzed with and without decoding.
public class CorruptFlacBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("flac/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The Monkey's Audio fixtures written by mac, each analyzed with and without decoding: the files of the highest levels and of several frames in one class
// (a frame there costs the decoder the most), the rest in another, so that the two run side by side.
public class CorruptMonkeysAudioFileTests
{
    internal static bool IsHeavy(string name) => name.Contains("insane", StringComparison.Ordinal) || name.Contains("extra-high", StringComparison.Ordinal) || name.Contains("frames-", StringComparison.Ordinal);

    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("monkeys-audio/external-", StringComparison.Ordinal) && !IsHeavy(n));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The TrueHD and MLP fixtures written by FFmpeg, each analyzed with and without decoding.
public class CorruptTrueHdFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("truehd/external-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The Monkey's Audio fixtures of the highest levels and of several frames.
public class CorruptMonkeysAudioHeavyFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("monkeys-audio/external-", StringComparison.Ordinal) && CorruptMonkeysAudioFileTests.IsHeavy(n));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The Monkey's Audio fixtures of versions 3.93 to 3.98 (monkeys_audio_legacy.py), each analyzed with and without decoding.
public class CorruptMonkeysAudioLegacyFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("monkeys-audio/legacy-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built Monkey's Audio fixtures, each analyzed (the decoder is held to the files mac wrote above: a built file is three frames of 160,000 samples).
public class CorruptMonkeysAudioBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("monkeys-audio/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The WAV fixtures written by FFmpeg and by lossyWAV, each analyzed with and without decoding and measuring.
public class CorruptWavFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("wav/external-", StringComparison.Ordinal) || n.StartsWith("wav/lossywav-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built WAV fixtures, each analyzed with and without decoding and measuring.
public class CorruptWavBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("wav/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The AIFF and AIFF-C fixtures, FFmpeg-written and hand-built, each analyzed with and without decoding and measuring.
public class CorruptAiffFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("aiff/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built TrueHD and MLP fixtures, each analyzed with and without decoding.
public class CorruptTrueHdBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("truehd/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The WavPack fixtures written by wavpack and FFmpeg, each analyzed with and without decoding (a share of the variants is decoded, as for Monkey's Audio).
public class CorruptWavPackFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("wavpack/external-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built WavPack fixtures.
public class CorruptWavPackBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("wavpack/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

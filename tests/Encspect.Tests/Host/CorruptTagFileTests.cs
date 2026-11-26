namespace Encspect.Tests.Host;

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

// The fixtures in tests/fixtures/mpeg-audio that LAME wrote.
public class CorruptLameFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg-audio/external-lame-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The other fixtures in tests/fixtures/mpeg-audio.
public class CorruptMpegFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg-audio/", StringComparison.Ordinal) && n.IndexOf('/', "mpeg-audio/".Length) < 0 && !n.StartsWith("mpeg-audio/external-lame-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/mpeg-audio/layer2 and mpeg-audio/layer3.
public class CorruptLayerFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg-audio/layer2/", StringComparison.Ordinal) || n.StartsWith("mpeg-audio/layer3/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/mpeg-audio/vbr and mpeg-audio/stats.
public class CorruptVbrFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("mpeg-audio/vbr/", StringComparison.Ordinal) || n.StartsWith("mpeg-audio/stats/", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The real files of encoders the CI cannot run (sample-kit) and the files with one stored checksum changed (damaged-units) of the MPEG audio, FLAC and WavPack folders; those of the
// other formats are in their format's class.
public class CorruptKitFileTests
{
    private static readonly string[] Folders = ["mpeg-audio/sample-kit/", "mpeg-audio/damaged-units/", "flac/sample-kit/", "flac/damaged-units/", "wavpack/damaged-units/"];

    public static TheoryData<string> Names => CorruptFiles.Names(n => Folders.Any(f => n.StartsWith(f, StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The fixtures in tests/fixtures/asf: the hand-built files of every object, the Windows Media files and the ones FFmpeg's muxer wrote, each analyzed with and without decoding the
// streams that have a decoder (MPEG audio and AC-3).
public class CorruptAsfFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("asf/", StringComparison.Ordinal));

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

// The FLAC fixtures written by flac and FFmpeg other than the compression-level ones, each analyzed with and without decoding.
public class CorruptFlacFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("flac/external-", StringComparison.Ordinal) && !n.Contains("-level-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The compression-level fixtures of flac and FFmpeg (the names with -level-), in a class of their own so that they run beside the others: with
// them, the one FLAC class took 25.6 minutes alone on a 4-core machine and its shard ran into the 60-minute limit on macos-15-intel.
public class CorruptFlacLevelFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("flac/external-", StringComparison.Ordinal) && n.Contains("-level-", StringComparison.Ordinal));

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

// The WavPack fixtures written by wavpack, FFmpeg and the original encoders of versions 1 to 4.31, each analyzed with and without decoding (a share of the variants is decoded, as for Monkey's Audio).
public class CorruptWavPackFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("wavpack/external-", StringComparison.Ordinal) || n.StartsWith("wavpack/legacy/external-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

// The hand-built WavPack fixtures.
public class CorruptWavPackBuiltFileTests
{
    public static TheoryData<string> Names => CorruptFiles.Names(n => n.StartsWith("wavpack/built-", StringComparison.Ordinal) || n.StartsWith("wavpack/legacy/built-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public Task MutatedFixturesProduceReports(string name) => CorruptFiles.MutatedFixturesProduceReports(name);
}

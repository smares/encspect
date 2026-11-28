using System.Text.RegularExpressions;

namespace Encspect.Tests.Host;

// Every finding code of every format and tag project, and the host's decode.* codes, is asserted by a test or listed by a fixture's manifest or decode golden, or named
// below with the reason it is not yet. A project added without an entry in Prefixes fails, so that a new project cannot land outside this test.
public partial class FindingCoverageTests
{
    // The prefixes of the codes each project emits (the part of a code before the dot).
    private static readonly Dictionary<string, string[]> Prefixes = new(StringComparer.Ordinal)
    {
        ["Encspect.Formats.Aac"] = ["aac"],
        ["Encspect.Formats.Ac3"] = ["ac-3"],
        ["Encspect.Formats.Aiff"] = ["aiff"],
        ["Encspect.Formats.Alac"] = ["alac"],
        ["Encspect.Formats.Asf"] = ["asf"],
        ["Encspect.Formats.Dts"] = ["dts"],
        ["Encspect.Formats.DtsHd"] = ["dts-hd"],
        ["Encspect.Formats.Flac"] = ["flac"],
        ["Encspect.Formats.Matroska"] = ["matroska"],
        ["Encspect.Formats.MonkeysAudio"] = ["monkeys-audio"],
        ["Encspect.Formats.Mp4"] = ["mp4"],
        ["Encspect.Formats.MpegAudio"] = ["mpeg-audio"],
        ["Encspect.Formats.Musepack"] = ["musepack"],
        ["Encspect.Formats.Ogg"] = ["ogg"],
        ["Encspect.Formats.OptimFrog"] = ["optimfrog"],
        ["Encspect.Formats.Opus"] = ["opus"],
        ["Encspect.Formats.Speex"] = ["speex"],
        ["Encspect.Formats.Tak"] = ["tak"],
        ["Encspect.Formats.TrueHd"] = ["truehd"],
        ["Encspect.Formats.Tta"] = ["tta"],
        ["Encspect.Formats.Vorbis"] = ["vorbis"],
        ["Encspect.Formats.Wav"] = ["wav", "pcm"],
        ["Encspect.Formats.WavPack"] = ["wavpack"],
        ["Encspect.Formats.Wma"] = ["wma"],
        ["Encspect.Tags.Ape"] = ["ape"],
        ["Encspect.Tags.Id3"] = ["id3v1", "id3v2"],
        ["Encspect.Tags.Lyrics3"] = ["lyrics3"],
        ["Encspect.Tags.VorbisComment"] = ["vorbis-comment"],
    };

    // The codes no test, manifest or decode golden asserts yet, each with why (0.5 works the list down): the code, then the reason.
    private const string NoInputYet = "the damage it reports is in none of the committed files, and no crafted input raises it yet";
    private const string ClaimNoInput = "an encoder or writer claim the parser reports only in a case no committed file shows (for example a claim that differs from another)";
    private const string NotCheckedNoInput = "a note that a checksum or a signaling is not checked: no committed file has the field that triggers it";
    private const string RouteNoInput = "a stream or object the parser reads no further (no audio, an unread payload, an object type not analyzed): no committed file takes the route that reports it";

    private static readonly Dictionary<string, string> NotYetCovered = new(StringComparer.Ordinal)
    {
        ["aac.adif-blocks-unread"] = NoInputYet,
        ["aac.adif-header-invalid"] = NoInputYet,
        ["aac.adif-header-truncated"] = NoInputYet,
        ["aac.block-positions-invalid"] = NoInputYet,
        ["aac.buffer-fullness-mixed"] = NoInputYet,
        ["aac.config-truncated"] = NoInputYet,
        ["aac.crc-not-verified"] = NotCheckedNoInput,
        ["aac.empty-access-units"] = NoInputYet,
        ["aac.encoder-claim-differs"] = ClaimNoInput,
        ["aac.error-protection-not-analyzed"] = RouteNoInput,
        ["aac.extension3"] = NoInputYet,
        ["aac.fill-payload-overrun"] = NoInputYet,
        ["aac.implicit-signaling-not-checked"] = NotCheckedNoInput,
        ["aac.object-type-not-analyzed"] = RouteNoInput,
        ["aac.pce-without-channels"] = NoInputYet,
        ["aac.pulse-out-of-range"] = NoInputYet,
        ["aac.reserved-profile"] = NoInputYet,
        ["aac.sbr-header-invalid"] = NoInputYet,
        ["aac.sbr-past-fill"] = NoInputYet,
        ["aac.store-mismatch"] = NoInputYet,
        ["aac.unknown-sync-extension"] = NoInputYet,
        ["aac.usac-reserved-frame-length"] = NoInputYet,
        ["ac-3.auxiliary-length"] = NoInputYet,
        ["ac-3.block-overrun"] = NoInputYet,
        ["ac-3.config-box-type"] = NoInputYet,
        ["ac-3.config-truncated"] = NoInputYet,
        ["ac-3.dependent-after-converted"] = NoInputYet,
        ["ac-3.emdf-limit"] = NoInputYet,
        ["ac-3.encoder-claim"] = ClaimNoInput,
        ["ac-3.header-overrun"] = NoInputYet,
        ["ac-3.little-endian"] = NoInputYet,
        ["ac-3.mix-data-overrun"] = NoInputYet,
        ["ac-3.substream-blocks-differ"] = NoInputYet,
        ["ape.footer-missing"] = NoInputYet,
        ["ape.header-flags"] = NoInputYet,
        ["ape.item-type-reserved"] = NoInputYet,
        ["ape.reserved-not-zero"] = NoInputYet,
        ["decode.frame-too-large"] = NoInputYet,
        ["id3v2.crc-not-verified"] = NotCheckedNoInput,
        ["id3v2.footer-mismatch"] = NoInputYet,
        ["id3v2.frame-content-truncated"] = NoInputYet,
        ["id3v2.frame-encrypted"] = NoInputYet,
        ["id3v2.frame-header-truncated"] = NoInputYet,
        ["id3v2.revision-invalid"] = NoInputYet,
        ["id3v2.seek-invalid"] = NoInputYet,
        ["lyrics3.fields-invalid"] = NoInputYet,
        ["lyrics3.image-invalid"] = NoInputYet,
        ["mp4.chan-descriptions-exceed-box"] = NoInputYet,
        ["mp4.chnl-version-not-read"] = NoInputYet,
        ["mp4.data-after-tag"] = NoInputYet,
        ["mp4.descriptor-overruns"] = NoInputYet,
        ["mp4.descriptor-truncated"] = NoInputYet,
        ["mp4.edit-list-rate"] = NoInputYet,
        ["mp4.edit-list-several-edits"] = NoInputYet,
        ["mp4.fragment-sample-outside-file"] = NoInputYet,
        ["mp4.ftyp-not-first"] = NoInputYet,
        ["mp4.itunsmpb-invalid"] = NoInputYet,
        ["mp4.key-size-invalid"] = NoInputYet,
        ["mp4.no-sample-description"] = NoInputYet,
        ["mp4.sample-description-count"] = NoInputYet,
        ["mp4.stsc-order"] = NoInputYet,
        ["mp4.stz2-field-size"] = NoInputYet,
        ["mpeg-audio.multichannel-in-extension"] = NoInputYet,
        ["mpeg-audio.multichannel-too-long"] = NoInputYet,
        ["musepack.writer-claim-differs"] = ClaimNoInput,
        ["ogg.packet-too-large"] = NoInputYet,
        ["ogg.pages-across-links"] = NoInputYet,
        ["opus.frame-overrun"] = NoInputYet,
        ["opus.granule-inconsistent"] = NoInputYet,
        ["opus.no-audio"] = RouteNoInput,
        ["opus.payload-not-analyzed"] = RouteNoInput,
        ["pcm.processor-claim-differs"] = ClaimNoInput,
        ["speex.comment-missing"] = NoInputYet,
        ["speex.comment-trailing-data"] = NoInputYet,
        ["speex.end-trimming-large"] = NoInputYet,
        ["speex.extra-headers-missing"] = NoInputYet,
        ["speex.header-extra-data"] = NoInputYet,
        ["speex.no-audio"] = RouteNoInput,
        ["speex.payload-not-analyzed"] = RouteNoInput,
        ["speex.rate-invalid"] = NoInputYet,
        ["vorbis-comment.truncated"] = NoInputYet,
        ["vorbis.codebook-invalid"] = NoInputYet,
        ["vorbis.header-extra-data"] = NoInputYet,
        ["vorbis.header-granule"] = NoInputYet,
        ["vorbis.header-missing"] = NoInputYet,
        ["vorbis.header-short"] = NoInputYet,
        ["vorbis.no-audio"] = RouteNoInput,
        ["vorbis.payload-not-analyzed"] = RouteNoInput,
    };

    [GeneratedRegex("\"((?:[a-z0-9]+(?:-[a-z0-9]+)*)\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex AnyCode();

    [GeneratedRegex("\"code\": \"([a-z0-9-]+\\.[a-z0-9-]+)\"")]
    private static partial Regex ManifestCodeAny();

    [Fact]
    public void EveryCodeOfEveryProjectIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string src = Path.Combine(root, "src");
        string[] projects = [.. Directory.EnumerateDirectories(src, "Encspect.*").Select(Path.GetFileName).Where(n => n!.StartsWith("Encspect.Formats.", StringComparison.Ordinal) || n.StartsWith("Encspect.Tags.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)!];
        Assert.Equal(Prefixes.Keys.Order(StringComparer.Ordinal), projects);

        static string[] Sources(string dir) => [.. Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

        // The codes each group emits: a project's own prefixes, and the decode.* codes of the host and of any project.
        var emitted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach ((string project, string[] prefixes) in Prefixes)
        {
            emitted[project] = [.. Sources(Path.Combine(src, project)).SelectMany(p => AnyCode().Matches(File.ReadAllText(p)).Select(m => m.Groups[1].Value))
                .Where(code => prefixes.Contains(code[..code.IndexOf('.')], StringComparer.Ordinal))];
        }

        emitted["Encspect.Host (decode)"] = [.. Directory.EnumerateDirectories(src, "Encspect.*").SelectMany(Sources)
            .SelectMany(p => AnyCode().Matches(File.ReadAllText(p)).Select(m => m.Groups[1].Value)).Where(code => code.StartsWith("decode.", StringComparison.Ordinal))];
        foreach ((string project, HashSet<string> codes) in emitted)
        {
            Assert.True(codes.Count > 0, $"{project} emits no code the test found.");
        }

        // A code counts where a test names it on a line that does not say the code is absent, or a fixture's manifest, decode golden or report golden lists it as a finding (compared exactly).
        var asserted = new HashSet<string>(StringComparer.Ordinal);
        string tests = Path.Combine(root, "tests", "Encspect.Tests");
        foreach (string path in Sources(tests).Where(p => !Path.GetFileName(p).StartsWith("FindingCoverage", StringComparison.Ordinal)))
        {
            foreach (string line in File.ReadLines(path).Where(l => !l.Contains("DoesNotContain", StringComparison.Ordinal) && !l.Contains("Assert.False", StringComparison.Ordinal)))
            {
                foreach (Match m in AnyCode().Matches(line))
                {
                    asserted.Add(m.Groups[1].Value);
                }
            }
        }

        // A finding in a golden report is raised by the fixture and compared exactly, as one in a manifest or decode golden is.
        const string key = "\"code\": \"";
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*.json", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".expected.json", StringComparison.Ordinal) || p.EndsWith(".decode.json", StringComparison.Ordinal) || p.EndsWith(".report.json", StringComparison.Ordinal)))
        {
            foreach (string line in File.ReadLines(path).Where(l => l.Contains(key, StringComparison.Ordinal)))
            {
                foreach (Match m in ManifestCodeAny().Matches(line))
                {
                    asserted.Add(m.Groups[1].Value);
                }
            }
        }

        var all = emitted.Values.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        string[] missing = [.. all.Where(code => !asserted.Contains(code) && !NotYetCovered.ContainsKey(code)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No test, manifest or decode golden asserts {missing.Length} codes, and NotYetCovered does not name them: {string.Join(", ", missing)}");
        string[] stale = [.. NotYetCovered.Keys.Where(code => !all.Contains(code) || asserted.Contains(code)).Order(StringComparer.Ordinal)];
        Assert.True(stale.Length == 0, $"NotYetCovered names codes that are asserted now or no longer emitted: {string.Join(", ", stale)}");
        Assert.All(NotYetCovered.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }
}

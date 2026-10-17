using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Musepack;

// The encoder of a Musepack stream. The encoder writes its own version into the stream: one byte at
// the end of the SV7 header, three in the SV8 encoder info packet. libmpcdec r495
// (mpc_get_encoder_string) reads them: an SV7 byte, or an SV8 version whose major × 100 + minor is at
// most 116, is a release, beta or alpha of the SV7 encoders by its last digit; a later SV8 version
// is stable where its minor version is even. Encodes confirm which SV7 encoders write what (the table
// below, with high confidence), and that mpcenc of musepack_src r495 writes 1.30.1 (mpcenc/config.h).
// mpc2sv8 converts SV7 to SV8 and writes the SV7 byte's hundreds as major, the rest as minor and
// build 0 (mpc2sv8.c), which it did for mppenc 1.16's 116 as 1.16.0. Other versions follow
// libmpcdec's reading with medium confidence. A claim of the encoder role (EncoderClaim, an ID3v2 TSSE
// frame) alone names the encoder as written with low confidence; the claims of the writer role (an APEv2 Encoder
// item, which Musepack SV8's chapters and trailing tags hold, and ffmpeg's muxer version) are resolved apart
// (WriterDetection).
internal static partial class MusepackEncoder
{
    private const int MaxClaimedName = 64;

    [GeneratedRegex(@"(?<![a-z])(mpcenc|mppenc|musepack|mpc2sv8|mpcgain|mp\+)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MusepackWords();

    // What each SV7 encoder writes into the header, from encodes with mppenc 1.16 built from source and
    // with every Rarewares Win32 build (scripts/fixtures/musepack_tool.py, which records each build's
    // fields in tests/fixtures/musepack/sv7-encoders.json). The version byte separates the releases from
    // 1.06 on; builds that write the same byte differ in other fields or are named together, as a range
    // that admits the untested builds between them. Before 1.06 every build writes 0: Klemm's 0.90f to
    // 1.0 without true gapless and with Max Level 0, Buschmann's 1.7.8 to 1.7.9c without true gapless and
    // with the input's Max Level, and the builds from 1.01j to 1.04 and the three in Rarewares' 1.05z
    // archives with true gapless. Max Level separates Klemm's builds from Buschmann's only where the
    // ReplayGain fields are 0, since ReplayGain tools fill it too (mppenc 1.16 encode_sv7.c); otherwise
    // both are named, with medium confidence. Some builds write profile 1 ("Unstable/Experimental") at
    // every setting tried (the default, --radio and --insane; sv7-encoders.json), where the others
    // write the profile asked for, which sets them apart; other settings were not tried. The rows are
    // tried in order.
    private static readonly Sv7Build[] Sv7Builds =
    [
        new(116, null, null, null, "mppenc 1.16", "mppenc 1.16 (built from source)"),
        new(115, null, null, true, "mppenc 1.15f to 1.15r", "mppenc 1.15f, 1.15i and 1.15r"),
        new(115, null, null, false, "mppenc 1.15t to 1.15u", "mppenc 1.15t and 1.15u"),
        new(114, null, null, null, "mppenc 1.14", "mppenc 1.14"),
        new(113, null, null, null, "mppenc 1.13a to 1.13g", "mppenc 1.13a and 1.13g"),
        new(112, null, null, null, "mppenc 1.12", "mppenc 1.12"),
        new(111, null, null, true, "mppenc 1.11a", "mppenc 1.11a"),
        new(111, null, null, false, "mppenc 1.11b", "mppenc 1.11b"),
        new(110, null, null, null, "mppenc 1.1", "mppenc 1.1"),
        new(106, null, null, null, "mppenc 1.06", "mppenc 1.06"),
        new(0, true, null, true, "mppenc 1.05z1 to 1.06 alpha", "mppenc 1.05z1, 1.05z2 and the 1.06 alpha of Rarewares' 1.05z3 archive"),
        new(0, true, null, false, "mppenc 1.01j to 1.04", "mppenc 1.01j, 1.02 and 1.04"),
        new(0, false, true, null, "MP+ 1.7.8 to 1.7.9c (Buschmann)", "MP+ 1.7.8, 1.7.9, 1.7.9a and 1.7.9c") { WithoutReplayGain = true },
        new(0, false, false, null, "mppenc 0.90f to 1.0 (Klemm)", "mppenc 0.90f, 0.90k, 0.90m, 0.90o, 0.90r and 1.0") { WithoutReplayGain = true },
        new(0, false, null, null, "MP+ 1.7.8 to 1.7.9c (Buschmann) or mppenc 0.90f to 1.0 (Klemm)", "these builds, none of which writes ReplayGain,")
        {
            Confidence = "Medium",
        },
    ];

    // A row of the table: the version byte and, where builds share it, whether true gapless is set,
    // whether Max Level is set and whether the profile is 1; null matches either. WithoutReplayGain
    // asks for the ReplayGain fields to be 0.
    private sealed record Sv7Build(int Byte, bool? Gapless, bool? MaxLevel, bool? ExperimentalProfile, string Name, string Confirmed)
    {
        public bool WithoutReplayGain { get; init; }

        public string Confidence { get; init; } = "High";

        public bool Matches(Sv7Header h) =>
            h.Encoder == Byte && (Gapless is null || h.Gapless == Gapless) && (MaxLevel is null || (h.MaxLevel != 0) == MaxLevel)
            && (ExperimentalProfile is null || (h.Profile == 1) == ExperimentalProfile)
            && (!WithoutReplayGain || h is { TitleGain: 0, TitlePeak: 0, AlbumGain: 0, AlbumPeak: 0 });

        public string Traits =>
            (Gapless is null ? string.Empty : Gapless.Value ? ", true gapless set" : ", true gapless not set")
            + (MaxLevel is null ? string.Empty : MaxLevel.Value ? ", Max Level set" : ", Max Level 0")
            + (ExperimentalProfile is null ? string.Empty : ExperimentalProfile.Value ? ", profile 1" : ", a profile other than 1")
            + (WithoutReplayGain ? ", the ReplayGain fields 0" : Byte == 0 && Gapless == false ? ", ReplayGain set, which makes Max Level no sign of the build" : string.Empty);
    }

    // libmpcdec r495's name for an SV7 version byte, or an SV8 version, of at most 116
    // (mpc_get_encoder_string).
    public static string LibmpcdecName(int version) => version switch
    {
        0 => "Buschmann 1.7.0 to 1.7.9 or Klemm 0.90 to 1.05",
        _ when version % 10 == 0 => $"Release {version / 100}.{version / 10 % 10}",
        _ when version % 2 == 0 => $"Beta {version / 100}.{version % 100:00}",
        _ => $"Alpha {version / 100}.{version % 100:00}",
    };

    // The name for the SV7 header's version byte: libmpcdec takes a byte above 116 into the branch of
    // the later versions, which reads major, minor and build from the bits above the byte, all 0
    // (mpc_get_encoder_string, where only SV8 recomputes the version first), and calls such a
    // version stable for its even minor version.
    public static string Sv7Name(int version) => version <= 116 ? LibmpcdecName(version) : "stable 0.0.0";

    public static string Sv8Name(int major, int minor, int build)
    {
        int version = (major * 100) + minor;
        if (version <= 116)
        {
            return LibmpcdecName(version);
        }

        return $"{(minor % 2 == 0 ? "stable" : "unstable")} {major}.{minor}.{build}";
    }

    // The Encoder node, covering the stream, or null where nothing names one. sv7 is the SV7 header,
    // sv8Version the encoder info packet's major, minor and build; field is the node that
    // holds them. differs reports a claim that names another encoder than the stream's evidence.
    public static Node? Detect(Sv7Header? sv7, (int Major, int Minor, int Build)? sv8Version, Node? field, IReadOnlyList<EncoderClaim> claims,
        Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string, string> differs)
    {
        Node? encoder = null;
        if (sv7 is { } header && field is not null)
        {
            string at = $"at 0x{field.Offset:X}";
            if (Sv7Builds.FirstOrDefault(b => b.Matches(header)) is { } build)
            {
                encoder = Named(cover, build.Name, build.Confidence,
                    $"The SV7 header's encoder version {at} is {header.Encoder}{build.Traits}, as encodes with {build.Confirmed} write it");
            }
            else if (header.Encoder <= 116)
            {
                encoder = Named(cover, $"Musepack encoder {LibmpcdecName(header.Encoder)}", "Medium",
                    $"The SV7 header's encoder version {at} is {header.Encoder}, which libmpcdec reads as {LibmpcdecName(header.Encoder)}; no encode confirms a release that writes it");
            }
            else
            {
                encoder = Named(cover, $"Musepack encoder, version byte {header.Encoder}", "Low",
                    $"The SV7 header's encoder version {at} is {header.Encoder}, past mppenc 1.16's 116, the last SV7 encoder; no encoder known writes it");
            }
        }
        else if (sv8Version is { } version && field is not null)
        {
            string at = $"at 0x{field.Offset:X}";
            string written = $"{version.Major}.{version.Minor}.{version.Build}";
            int combined = (version.Major * 100) + version.Minor;
            if (version is (1, 30, 1))
            {
                encoder = Named(cover, "mpcenc 1.30.1", "High", $"The encoder info packet {at} names version 1.30.1, which mpcenc 1.30.1 writes");
            }
            else if (combined == 116 && version.Build == 0)
            {
                encoder = Named(cover, "mppenc 1.16, converted to SV8 by mpc2sv8", "High",
                    $"The encoder info packet {at} names version 1.16.0, which mpc2sv8 writes for a stream mppenc 1.16 encoded");
            }
            else if (combined <= 116 && version.Build == 0)
            {
                encoder = Named(cover, $"Musepack encoder {LibmpcdecName(combined)}, converted to SV8 by mpc2sv8", "Medium",
                    $"The encoder info packet {at} names version {written}, which mpc2sv8 writes for an SV7 stream whose encoder version libmpcdec reads as {LibmpcdecName(combined)}");
            }
            else
            {
                encoder = Named(cover, $"mpcenc {written}", "Medium",
                    $"The encoder info packet {at} names version {written}, which libmpcdec reads as {Sv8Name(version.Major, version.Minor, version.Build)}");
            }
        }

        foreach (EncoderClaim claim in ClaimRoles.Encoders(claims))
        {
            string text = claim.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            string evidence = $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{text}\"";
            if (encoder is null)
            {
                encoder = Named(cover, text.Length > MaxClaimedName ? text[..MaxClaimedName] : text, "Low", evidence);
                continue;
            }

            if (MusepackWords().IsMatch(text) && MusepackWords().IsMatch(encoder.Value?.ToString() ?? string.Empty))
            {
                encoder.Add(cover("Evidence", evidence, Explain.Evidence));
            }
            else
            {
                differs(claim, text, encoder.Value!.ToString());
            }
        }

        return encoder;
    }

    public static Confidence? ConfidenceOf(Node? encoder) =>
        encoder?.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString() is { } text
        && Enum.TryParse(text, out Confidence parsed) ? parsed : null;

    private static Node Named(Func<string, NodeValue?, string, Node> cover, string name, string confidence, string evidence)
    {
        Node encoder = cover("Encoder", name, Explain.Encoder);
        encoder.Add(cover("Confidence", confidence, Explain.Confidence));
        encoder.Add(cover("Evidence", evidence, Explain.Evidence));
        return encoder;
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

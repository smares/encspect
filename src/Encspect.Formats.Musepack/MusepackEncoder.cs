using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Musepack;

// The encoder of a Musepack stream. The encoder writes its own version into the stream: one byte at
// the end of the SV7 header, three in the SV8 encoder info packet. libmpcdec r495
// (mpc_get_encoder_string) reads them: an SV7 byte, or an SV8 version whose major × 100 + minor is at
// most 116, is a release, beta or alpha of the SV7 encoders by its last digit; a later SV8 version
// is stable where its minor version is even. Two versions are confirmed by encodes: mppenc 1.16,
// built from source, writes 116, and mpcenc of musepack_src r495 writes 1.30.1 (mpcenc/config.h).
// mpc2sv8 converts SV7 to SV8 and writes the SV7 byte's hundreds as major, the rest as minor and
// build 0 (mpc2sv8.c), which it did for mppenc 1.16's 116 as 1.16.0. Other versions follow
// libmpcdec's reading with medium confidence. A tag's claim (EncoderClaim) alone names the
// encoder as written with low confidence.
internal static partial class MusepackEncoder
{
    private const int MaxClaimedName = 64;

    [GeneratedRegex(@"(?<![a-z])(mpcenc|mppenc|musepack|mpc2sv8|mpcgain)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MusepackWords();

    // libmpcdec r495's name for an SV7 version byte, or an SV8 version of at most 1.16.
    public static string LibmpcdecName(int version) => version switch
    {
        0 => "Buschmann 1.7.0 to 1.7.9 or Klemm 0.90 to 1.05",
        _ when version % 10 == 0 => $"Release {version / 100}.{version / 10 % 10}",
        _ when version % 2 == 0 => $"Beta {version / 100}.{version % 100:00}",
        _ => $"Alpha {version / 100}.{version % 100:00}",
    };

    public static string Sv8Name(int major, int minor, int build)
    {
        int version = (major * 100) + minor;
        if (version <= 116)
        {
            return LibmpcdecName(version);
        }

        return $"{(minor % 2 == 0 ? "stable" : "unstable")} {major}.{minor}.{build}";
    }

    // The Encoder node, covering the stream, or null where nothing names one. sv7Version is the SV7
    // byte, sv8Version the encoder info packet's major, minor and build; field is the node that
    // holds them. differs reports a claim that names another encoder than the stream's evidence.
    public static Node? Detect(int? sv7Version, (int Major, int Minor, int Build)? sv8Version, Node? field, IReadOnlyList<EncoderClaim> claims,
        Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string, string> differs)
    {
        Node? encoder = null;
        if (sv7Version is { } byteValue && field is not null)
        {
            string at = $"at 0x{field.Offset:X}";
            if (byteValue == 116)
            {
                encoder = Named(cover, "mppenc 1.16", "High",
                    $"The SV7 header's encoder version {at} is 116, which mppenc 1.16 writes");
            }
            else if (byteValue == 0)
            {
                encoder = Named(cover, LibmpcdecName(0), "Low",
                    $"The SV7 header's encoder version {at} is 0, which libmpcdec names so; no encoder of those versions was at hand to confirm it");
            }
            else
            {
                encoder = Named(cover, $"Musepack encoder {LibmpcdecName(byteValue)}", "Medium",
                    $"The SV7 header's encoder version {at} is {byteValue}, which libmpcdec reads as {LibmpcdecName(byteValue)}");
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

        foreach (EncoderClaim claim in claims)
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

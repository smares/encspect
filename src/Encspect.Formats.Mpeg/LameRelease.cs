using System.Globalization;
using System.Text.RegularExpressions;

namespace Encspect.Formats.Mpeg;

// A LAME version as LAME writes it in either of its two places (libmp3lame/version.c):
// - the LAME tag's nine bytes, get_lame_very_short_version: "LAME", major.minor and a marker, "a"
//   for an alpha, "b" for a beta, "r" for a release with a patch level above 0 and a space
//   otherwise, then the patch level where it fits ("LAME3.98b", "LAME3.99r", "LAME3.91 "). 3.99
//   alphas wrote "L3.99a12"; "LAME3.100" and later fill the bytes before any marker, and
//   HydrogenAudio's 3.90.3 wrote "LAME3.90." with its patch level cut off;
// - the ancillary data, get_lame_short_version after "LAME": "3.94 (beta)", "3.98 (beta 8)",
//   "3.99 (alpha 12)", "3.99.5", "3.100".
internal sealed partial class LameRelease
{
    private const string Plain = "plain";
    private const string Release = "release";

    private LameRelease(string major, string minor, string kind, int? patch, bool hasNoMarkerRoom)
    {
        Major = major;
        Minor = minor;
        Kind = kind;
        Patch = patch;
        HasNoMarkerRoom = hasNoMarkerRoom;
    }

    public string Major { get; }

    public string Minor { get; }

    // "alpha", "beta", "release" (a patch level above 0), or "plain" (no marker).
    public string Kind { get; }

    public int? Patch { get; }

    // Nine bytes of version with no marker in them: the tag had no room left for one.
    public bool HasNoMarkerRoom { get; }

    public string Name => Kind switch
    {
        Plain => $"LAME {Major}.{Minor}",
        Release => Patch is { } patch ? $"LAME {Major}.{Minor}.{patch}" : $"LAME {Major}.{Minor}.x",
        _ => Patch is { } number ? $"LAME {Major}.{Minor} ({Kind} {number})" : $"LAME {Major}.{Minor} ({Kind})",
    };

    [GeneratedRegex(@"^(?:LAME|L(?=\d))(\d+)\.(\d+)(.*)$")]
    private static partial Regex Version();

    [GeneratedRegex(@"^(?:(?<release>\.(?<patch>\d*)|r(?<patch>\d*))|(?<alpha>a(?<patch>\d*)| \(alpha(?: (?<patch>\d+))?\))|(?<beta>b(?<patch>\d*)| \(beta(?: (?<patch>\d+))?\)))$")]
    private static partial Regex Marker();

    public static LameRelease? Parse(string text)
    {
        string raw = text.TrimEnd('\0');
        Match version = Version().Match(raw.TrimEnd(' '));
        if (!version.Success)
        {
            return null;
        }

        string major = version.Groups[1].Value;
        string minor = version.Groups[2].Value;
        string tail = version.Groups[3].Value;
        if (tail.Length == 0)
        {
            return new LameRelease(major, minor, Plain, null, raw.Length >= 9 && !raw.EndsWith(' '));
        }

        Match marker = Marker().Match(tail);
        if (!marker.Success)
        {
            return null;
        }

        string kind = marker.Groups["release"].Success ? Release : marker.Groups["alpha"].Success ? "alpha" : "beta";
        string patch = marker.Groups["patch"].Value;
        int? number = patch.Length is > 0 and < 6 ? int.Parse(patch, CultureInfo.InvariantCulture) : null;
        return new LameRelease(major, minor, kind, number, false);
    }

    // Whether other, read from the ancillary data, can be the version this one, read from the
    // tag, stands for.
    public bool Agrees(LameRelease other)
    {
        if (Major != other.Major || Minor != other.Minor)
        {
            return false;
        }

        if (HasNoMarkerRoom && Kind == Plain)
        {
            return true;
        }

        return Kind == other.Kind && (Patch is null || other.Patch is null || Patch == other.Patch);
    }

    // Whether this version, which agrees with the tag's, says more than the tag's does.
    public bool IsMoreSpecificThan(LameRelease tag) =>
        Name != tag.Name && ((Patch is not null && tag.Patch is null) || (tag.HasNoMarkerRoom && tag.Kind == Plain && Kind != Plain));
}

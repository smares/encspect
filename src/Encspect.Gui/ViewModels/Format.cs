using System.Globalization;
using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// How figures read in the GUI. Sorting never goes through these strings: rows sort by the typed
// figures they are made from.
public static class Format
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // "3:25" and "1:02:03"; under a minute with tenths, "0:05.3", since short files are common
    // among test files and fixtures.
    public static string Duration(double seconds)
    {
        // Rounded first, so 59.96 s reads as a minute rather than "0:60.0".
        double tenths = Math.Round(seconds, 1, MidpointRounding.AwayFromZero);
        if (tenths < 60)
        {
            return "0:" + tenths.ToString("00.0", Invariant);
        }

        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours >= 1
            ? ((int)time.TotalHours).ToString(Invariant) + time.ToString(@"\:mm\:ss", Invariant)
            : time.ToString(@"m\:ss", Invariant);
    }

    // A point in time on a graph's axis, in whole seconds: "0:05", "3:25", "1:02:03".
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? ((int)time.TotalHours).ToString(Invariant) + time.ToString(@"\:mm\:ss", Invariant)
            : time.ToString(@"m\:ss", Invariant);
    }

    public static string Bitrate(double kbps, bool? isVbr)
    {
        string text = Math.Round(kbps).ToString("0", Invariant) + " kbps";
        return isVbr switch
        {
            true => text + " VBR",
            false => text + " CBR",
            null => text,
        };
    }

    public static string SampleRate(int hertz) => (hertz / 1000.0).ToString("0.###", Invariant) + " kHz";

    public static string Encoder(string encoder, Confidence? confidence) =>
        confidence is { } level ? $"{encoder} ({level.ToString().ToLowerInvariant()})" : encoder;

    public static string Count(long count, string singular, string plural) =>
        count.ToString("N0", Invariant) + " " + (count == 1 ? singular : plural);

    public static string Hex(long offset) => "0x" + offset.ToString("X", Invariant);

    // The location of a node as the CLI's --offsets writes it: "0x1A, 4 bytes", "0x1+3, 2 bits",
    // "0x1A, 40 bytes in 0x1A 2 bytes, 0x40 2 bytes", "0x1+3, 2 bits, LSB first" for a Vorbis field.
    public static string Location(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        string text = node.BitOffset is { } bitOffset && node.BitLength is { } bitLength
            ? $"{Hex(node.Offset)}+{bitOffset.ToString(Invariant)}, {Count(bitLength, "bit", "bits")}{(node.BitOrder == BitOrder.LeastSignificantFirst ? ", LSB first" : string.Empty)}"
            : $"{Hex(node.Offset)}, {Count(node.Length, "byte", "bytes")}";
        if (node.Ranges is { } ranges)
        {
            text += " in " + string.Join(", ", ranges.Select(r => $"{Hex(r.Offset)} {Count(r.Length, "byte", "bytes")}"));
        }

        return text;
    }
}

using System.Globalization;

namespace Encspect.Core;

// A count in running text, with its noun in the singular for one ("1 byte", "2 bytes").
public static class Counts
{
    public static string Of(long count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    public static string Of(ulong count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    public static string Bytes(long count) => Of(count, "byte", "bytes");

    public static string Bits(long count) => Of(count, "bit", "bits");
}

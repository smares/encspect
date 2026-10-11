using System.Text;

namespace Encspect.Tags.Id3;

// Text encodings of ID3v2 frames (ID3v2.4 main structure, section 4): $00 ISO-8859-1,
// $01 UTF-16 with BOM, $02 UTF-16BE without BOM, $03 UTF-8. 2.2 and 2.3 define only $00
// and $01. Strings in the two-byte encodings end with $00 $00, the others with $00.
internal static class Id3v2Text
{
    public static string EncodingName(byte encoding) => encoding switch
    {
        0 => "ISO-8859-1",
        1 => "UTF-16",
        2 => "UTF-16BE",
        3 => "UTF-8",
        _ => $"Unknown ({encoding})",
    };

    public static int TerminatorWidth(byte encoding) => encoding is 1 or 2 ? 2 : 1;

    // Index of the terminator at or after start (aligned to the string start for two-byte
    // encodings), or end when the string runs to the end of the data.
    public static int FindTerminator(byte[] data, int start, int end, byte encoding)
    {
        if (TerminatorWidth(encoding) == 1)
        {
            int index = Array.IndexOf(data, (byte)0, start, end - start);
            return index < 0 ? end : index;
        }

        for (int i = start; i + 1 < end; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                return i;
            }
        }

        return end;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LE = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BE = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    // bigEndian carries the byte order of the last UTF-16 BOM into later strings of the same
    // frame. isCut says the string was cut short by the decode limit, so a character split there
    // is dropped rather than reported as invalid.
    public static DecodedText Decode(byte[] data, int start, int end, byte encoding, bool isCut, ref bool? bigEndian)
    {
        switch (encoding)
        {
            case 1:
                bool? bom = null;
                if (end - start >= 2 && data[start] == 0xFF && data[start + 1] == 0xFE)
                {
                    bom = false;
                    start += 2;
                }
                else if (end - start >= 2 && data[start] == 0xFE && data[start + 1] == 0xFF)
                {
                    bom = true;
                    start += 2;
                }

                // A non-empty string without a BOM, which ID3v2.3 section 3.3 and ID3v2.4 section
                // 4 require of every UTF-16 string.
                bool missingBom = bom is null && end > start;
                if (bom is not null || missingBom)
                {
                    bigEndian = bom ?? bigEndian ?? false;
                }

                return Utf16(data, start, end, bigEndian == true, isCut) with { MissingBom = missingBom, Bom = bom };
            case 2:
                return Utf16(data, start, end, bigEndian: true, isCut);
            case 3:
                return Utf8(data, start, end, isCut);
            default:
                return new DecodedText(Encoding.Latin1.GetString(data, start, end - start));
        }
    }

    private static DecodedText Utf16(byte[] data, int start, int end, bool bigEndian, bool isCut)
    {
        int length = end - start;
        bool isInvalid = false;
        if (length % 2 != 0)
        {
            // A lone $00 is half a terminator; any other odd byte is text a reader loses.
            isInvalid = !isCut && data[end - 1] != 0;
            length--;
        }

        if (isCut && length >= 2)
        {
            int last = bigEndian ? (data[start + length - 2] << 8) | data[start + length - 1] : (data[start + length - 1] << 8) | data[start + length - 2];
            if (last is >= 0xD800 and <= 0xDBFF)
            {
                length -= 2;
            }
        }

        (string text, bool undecodable) = Strict(bigEndian ? StrictUtf16BE : StrictUtf16LE, bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode, data, start, length);
        return new DecodedText(text) { IsInvalid = isInvalid || undecodable };
    }

    private static DecodedText Utf8(byte[] data, int start, int end, bool isCut)
    {
        int length = end - start;
        if (isCut && length > 0)
        {
            // Drop a multi-byte sequence that the limit cut short: back up to its lead byte.
            int lead = length - 1;
            while (lead > 0 && length - lead < 4 && (data[start + lead] & 0xC0) == 0x80)
            {
                lead--;
            }

            byte b = data[start + lead];
            int needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            if (length - lead < needed)
            {
                length = lead;
            }
        }

        (string text, bool undecodable) = Strict(StrictUtf8, Encoding.UTF8, data, start, length);
        return new DecodedText(text) { IsInvalid = undecodable };
    }

    // Invalid bytes are still shown, as U+FFFD, the way lenient readers show them.
    private static (string Text, bool Undecodable) Strict(Encoding strict, Encoding lenient, byte[] data, int start, int length)
    {
        try
        {
            return (strict.GetString(data, start, length), false);
        }
        catch (DecoderFallbackException)
        {
            return (lenient.GetString(data, start, length), true);
        }
    }
}

// Bom is the byte order a UTF-16 byte order mark named (true for big-endian), or null.
internal readonly record struct DecodedText(string Text)
{
    public bool MissingBom { get; init; }

    public bool? Bom { get; init; }

    public bool IsInvalid { get; init; }
}

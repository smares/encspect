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

    // bigEndian carries the byte order of the last UTF-16 BOM into later strings of the same
    // frame. missingBom reports a non-empty UTF-16 string without a BOM, which ID3v2.3 section 3.3
    // and ID3v2.4 section 4 require of every string.
    public static string Decode(byte[] data, int start, int end, byte encoding, ref bool? bigEndian, out bool missingBom)
    {
        missingBom = false;
        int length = end - start;

        switch (encoding)
        {
            case 1:
                if (length >= 2 && data[start] == 0xFF && data[start + 1] == 0xFE)
                {
                    bigEndian = false;
                    start += 2;
                }
                else if (length >= 2 && data[start] == 0xFE && data[start + 1] == 0xFF)
                {
                    bigEndian = true;
                    start += 2;
                }
                else if (length > 0)
                {
                    missingBom = true;
                    bigEndian ??= false;
                }

                length = end - start;
                length -= length % 2;
                return (bigEndian == true ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(data, start, length);
            case 2:
                length -= length % 2;
                return Encoding.BigEndianUnicode.GetString(data, start, length);
            case 3:
                return Encoding.UTF8.GetString(data, start, length);
            default:
                return Encoding.Latin1.GetString(data, start, length);
        }
    }
}

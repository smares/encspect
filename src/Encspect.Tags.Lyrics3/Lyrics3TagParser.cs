using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Tags.Lyrics3;

// Lyrics3 v1 and v2.00 (id3.org, "Lyrics3" and "Lyrics3 v2.00"). Both sit directly before an
// ID3v1 tag and are found from their end marker; whether ID3v1 follows is the host's layout
// check, since a tag parser does not know its neighbors.
public sealed class Lyrics3TagParser : ITagParser
{
    private const int MarkerSize = 9;
    private const int SizeFieldLength = 6;
    private const int MaxV1Lyrics = 5100;

    private static readonly byte[] Begin = "LYRICSBEGIN"u8.ToArray();
    private static readonly byte[] EndV1 = "LYRICSEND"u8.ToArray();
    private static readonly byte[] EndV2 = "LYRICS200"u8.ToArray();

    // Lyrics3 v2.00 field IDs: indications, lyrics, additional information, lyrics author,
    // extended album, artist and track title, image links.
    private static readonly HashSet<string> KnownFields = ["IND", "LYR", "INF", "AUT", "EAL", "EAR", "ETT", "IMG"];

    public string Name => "Lyrics3";

    public TagProbe? ProbeBefore(ByteReader reader, long end)
    {
        long marker = end - MarkerSize;
        if (reader.MatchesAt(marker, EndV2))
        {
            return ParseV2(reader, end, marker);
        }

        if (reader.MatchesAt(marker, EndV1))
        {
            return ParseV1(reader, end, marker);
        }

        return null;
    }

    // v1 has no size field: the lyrics run from LYRICSBEGIN to LYRICSEND and hold at most 5100
    // bytes, so the search for LYRICSBEGIN is bounded by that limit.
    private TagProbe ParseV1(ByteReader reader, long end, long marker)
    {
        long windowStart = Math.Max(reader.Start, marker - MaxV1Lyrics - Begin.Length);
        byte[] window = reader.ReadBytesAt(windowStart, (int)(marker - windowStart));
        int index = window.AsSpan().LastIndexOf(Begin);
        if (index < 0)
        {
            return TagProbe.Reject(marker, "no LYRICSBEGIN within the 5100 bytes before LYRICSEND");
        }

        long start = windowStart + index;

        // Lyrics3 v1: "A byte in the text must not have the binary value 255." $00 is ruled out
        // as well: v1 does not say so, but its successor v2.00 restricts text to 01 to 254, and
        // zero bytes are what padding and binary data look like, not lyrics.
        int bad = window.AsSpan(index + Begin.Length).IndexOfAny((byte)0x00, (byte)0xFF);
        if (bad >= 0)
        {
            long offset = start + Begin.Length + bad;
            byte value = window[index + Begin.Length + bad];
            return TagProbe.Reject(marker, $"the lyrics contain the byte ${value:X2} at 0x{offset:X}, which is not lyrics text");
        }

        var tag = new Node(Name, start, end - start, "1.00");
        tag.Add("Begin", start, Begin.Length, "LYRICSBEGIN");
        long lyricsStart = start + Begin.Length;
        string lyrics = Encoding.Latin1.GetString(window, index + Begin.Length, (int)(marker - lyricsStart));
        tag.Add("Lyrics", lyricsStart, marker - lyricsStart, lyrics);
        tag.Add("End", marker, MarkerSize, "LYRICSEND");

        var evidence = new List<Evidence>
        {
            new("End marker \"LYRICSEND\"", marker, MarkerSize),
            new($"\"LYRICSBEGIN\" {marker - start} bytes earlier, within the 5100-byte limit", start, Begin.Length),
        };

        return TagProbe.Accept(tag, marker, Confidence.Medium, evidence, []);
    }

    // v2.00: LYRICSBEGIN, fields (3-letter ID, 5-digit size, data), a 6-digit size covering
    // LYRICSBEGIN and the fields, then LYRICS200.
    private TagProbe ParseV2(ByteReader reader, long end, long marker)
    {
        long sizeStart = marker - SizeFieldLength;
        if (sizeStart < reader.Start)
        {
            return TagProbe.Reject(marker, "there is no room for the size field before LYRICS200");
        }

        string sizeText = Encoding.Latin1.GetString(reader.ReadBytesAt(sizeStart, SizeFieldLength));
        if (!sizeText.All(char.IsAsciiDigit))
        {
            return TagProbe.Reject(marker, $"the size field \"{sizeText}\" is not six decimal digits");
        }

        long size = long.Parse(sizeText, CultureInfo.InvariantCulture);
        long start = sizeStart - size;
        if (size < Begin.Length || start < reader.Start || !reader.MatchesAt(start, Begin))
        {
            return TagProbe.Reject(marker, $"the size field {size} does not point at LYRICSBEGIN");
        }

        byte[] data = reader.ReadBytesAt(start, (int)size);
        var tag = new Node(Name, start, end - start, "2.00");
        var findings = new List<Finding>();
        tag.Add("Begin", start, Begin.Length, "LYRICSBEGIN");

        if (size == Begin.Length)
        {
            return TagProbe.Reject(marker, "the tag has no fields; Lyrics3 v2.00 requires at least one");
        }

        int pos = Begin.Length;
        while (pos < data.Length)
        {
            if (data.Length - pos < 8)
            {
                return TagProbe.Reject(marker, $"a field header at 0x{start + pos:X} is cut off by the size field");
            }

            string id = Encoding.Latin1.GetString(data, pos, 3);
            string fieldSizeText = Encoding.Latin1.GetString(data, pos + 3, 5);
            if (!id.All(char.IsAsciiLetterUpper) || !fieldSizeText.All(char.IsAsciiDigit))
            {
                return TagProbe.Reject(marker, $"the field at 0x{start + pos:X} has no valid ID and size, so the fields do not parse");
            }

            int fieldSize = int.Parse(fieldSizeText, CultureInfo.InvariantCulture);
            if (fieldSize > data.Length - pos - 8)
            {
                return TagProbe.Reject(marker, $"field {id} runs past the size field, so the fields do not end where the size says");
            }

            // Lyrics3 v2.00: field data consists of characters 01 to 254.
            int bad = data.AsSpan(pos + 8, fieldSize).IndexOfAny((byte)0x00, (byte)0xFF);
            if (bad >= 0)
            {
                return TagProbe.Reject(marker, $"field {id} contains the byte ${data[pos + 8 + bad]:X2}, outside the 01 to 254 that Lyrics3 v2.00 allows");
            }

            long fieldStart = start + pos;
            string value = Encoding.Latin1.GetString(data, pos + 8, fieldSize);
            Node field = tag.Add(id, fieldStart, 8 + fieldSize, value);
            field.Add("ID", fieldStart, 3, id);
            field.Add("Size", fieldStart + 3, 5, fieldSize.ToString(CultureInfo.InvariantCulture));
            field.Add("Data", fieldStart + 8, fieldSize, value);

            if (id == "IND")
            {
                if (pos != Begin.Length)
                {
                    findings.Add(new Finding(Severity.Warning, "lyrics3.ind-not-first", "The IND field must be the first field when it is present.", field));
                }

                // Characters: lyrics present, timestamps in the lyrics, inhibit random selection.
                field.Add("HasLyrics", fieldStart + 8, fieldSize, Bool(value, 0));
                field.Add("HasTimestamps", fieldStart + 8, fieldSize, Bool(value, 1));
                field.Add("IsRandomSelectionInhibited", fieldStart + 8, fieldSize, Bool(value, 2));
            }
            else if (!KnownFields.Contains(id))
            {
                findings.Add(new Finding(Severity.Info, "lyrics3.unknown-field", $"Lyrics3 field ID {id} is not defined by Lyrics3 v2.00.", field));
            }

            pos += 8 + fieldSize;
        }

        tag.Add("Size", sizeStart, SizeFieldLength, size.ToString(CultureInfo.InvariantCulture));
        tag.Add("End", marker, MarkerSize, "LYRICS200");

        var evidence = new List<Evidence>
        {
            new("End marker \"LYRICS200\"", marker, MarkerSize),
            new($"Size field {size} points at \"LYRICSBEGIN\"", sizeStart, SizeFieldLength),
            new("Fields end exactly at the size field", start, size),
        };

        return TagProbe.Accept(tag, marker, Confidence.High, evidence, findings);
    }

    private static string Bool(string indications, int index) =>
        index < indications.Length && indications[index] == '1' ? "true" : "false";
}

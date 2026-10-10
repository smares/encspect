using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Tags.Lyrics3;

// Lyrics3 v1 and v2.00 (id3.org, "Lyrics3" and "Lyrics3 v2.00"). Both sit directly before an
// ID3v1 tag and are found from their end marker. A tag is rejected only when its extent cannot
// be determined; spec violations inside it are findings that lower the confidence.
public sealed class Lyrics3TagParser : ITagParser
{
    private const int MarkerSize = 9;
    private const int SizeFieldLength = 6;
    private const int MaxV1Lyrics = 5100;

    // Oversized v1 lyrics are still found, as a spec violation, up to this distance.
    private const int MaxV1Search = 1 << 16;

    private static readonly byte[] Begin = "LYRICSBEGIN"u8.ToArray();
    private static readonly byte[] EndV1 = "LYRICSEND"u8.ToArray();
    private static readonly byte[] EndV2 = "LYRICS200"u8.ToArray();

    // Lyrics3 v2.00 field IDs: indications, lyrics, additional information, lyrics author,
    // extended album, artist and track title, image links.
    private static readonly HashSet<string> KnownFields = ["IND", "LYR", "INF", "AUT", "EAL", "EAR", "ETT", "IMG"];

    public string Name => "Lyrics3";

    public string? MustPrecede => "ID3v1";

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

    // v1 has no size field: the lyrics run from LYRICSBEGIN to LYRICSEND and should hold at most
    // 5100 bytes, so the nearest LYRICSBEGIN before the end marker starts the tag.
    private TagProbe ParseV1(ByteReader reader, long end, long marker)
    {
        long windowStart = Math.Max(reader.Start, marker - MaxV1Search - Begin.Length);
        byte[] window = reader.ReadBytesAt(windowStart, (int)(marker - windowStart));
        int index = window.AsSpan().LastIndexOf(Begin);
        if (index < 0)
        {
            return TagProbe.Reject(marker, "there is no LYRICSBEGIN before LYRICSEND");
        }

        long start = windowStart + index;
        long lyricsStart = start + Begin.Length;
        long lyricsLength = marker - lyricsStart;
        var tag = new Node(Name, start, end - start, "1.00");
        tag.Add("Begin", start, Begin.Length, "LYRICSBEGIN");
        string lyrics = Encoding.Latin1.GetString(window, index + Begin.Length, (int)lyricsLength);
        Node lyricsNode = tag.Add("Lyrics", lyricsStart, lyricsLength, lyrics);
        tag.Add("End", marker, MarkerSize, "LYRICSEND");

        var findings = new List<Finding>();
        var evidence = new List<Evidence>
        {
            new("End marker \"LYRICSEND\"", marker, MarkerSize),
            new($"\"LYRICSBEGIN\" {marker - start} bytes earlier", start, Begin.Length),
        };

        if (lyricsLength > MaxV1Lyrics)
        {
            findings.Add(new Finding(Severity.Warning, "lyrics3.too-long", $"The lyrics are {lyricsLength} bytes long; Lyrics3 v1 allows at most 5100.", lyricsNode));
        }

        // Lyrics3 v1: "A byte in the text must not have the binary value 255." $00 is reported as
        // well: v1 does not mention it, but its successor v2.00 restricts text to 01 to 254, and
        // zero bytes are what padding and binary data look like, not lyrics.
        int bad = window.AsSpan(index + Begin.Length).IndexOfAny((byte)0x00, (byte)0xFF);
        if (bad >= 0)
        {
            byte value = window[index + Begin.Length + bad];
            findings.Add(new Finding(
                Severity.Warning,
                "lyrics3.invalid-byte",
                $"The lyrics contain the byte ${value:X2} at 0x{lyricsStart + bad:X}, which is not lyrics text.",
                lyricsNode));
        }

        Confidence confidence = findings.Count > 0 ? Confidence.Low : Confidence.Medium;
        return TagProbe.Accept(tag, marker, confidence, evidence, findings);
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

        var evidence = new List<Evidence>
        {
            new("End marker \"LYRICS200\"", marker, MarkerSize),
            new($"Size field {size} points at \"LYRICSBEGIN\"", sizeStart, SizeFieldLength),
        };
        Confidence confidence = Confidence.High;

        if (size == Begin.Length)
        {
            findings.Add(new Finding(Severity.Warning, "lyrics3.no-fields", "The tag has no fields; Lyrics3 v2.00 requires at least one.", tag));
            confidence = Confidence.Low;
        }

        int pos = Begin.Length;
        while (pos < data.Length)
        {
            string? problem = null;
            int fieldSize = 0;
            string id = string.Empty;
            if (data.Length - pos < 8)
            {
                problem = $"The field header at 0x{start + pos:X} is cut off by the size field.";
            }
            else
            {
                id = Encoding.Latin1.GetString(data, pos, 3);
                string fieldSizeText = Encoding.Latin1.GetString(data, pos + 3, 5);
                if (!id.All(char.IsAsciiLetterUpper) || !fieldSizeText.All(char.IsAsciiDigit))
                {
                    problem = $"The bytes at 0x{start + pos:X} are not a field ID and size.";
                }
                else
                {
                    fieldSize = int.Parse(fieldSizeText, CultureInfo.InvariantCulture);
                    if (fieldSize > data.Length - pos - 8)
                    {
                        problem = $"Field {id} runs past the size field.";
                    }
                }
            }

            if (problem is not null)
            {
                Node unparsed = tag.Add("Unparsed", start + pos, data.Length - pos, $"{data.Length - pos} bytes");
                findings.Add(new Finding(Severity.Warning, "lyrics3.fields-invalid", $"{problem} The rest of the fields is not parsed.", unparsed));
                confidence = Confidence.Low;
                break;
            }

            long fieldStart = start + pos;
            string value = Encoding.Latin1.GetString(data, pos + 8, fieldSize);
            Node field = tag.Add(id, fieldStart, 8 + fieldSize, value);
            field.Add("ID", fieldStart, 3, id);
            field.Add("Size", fieldStart + 3, 5, fieldSize.ToString(CultureInfo.InvariantCulture));
            field.Add("Data", fieldStart + 8, fieldSize, value);

            // Lyrics3 v2.00: field data consists of characters 01 to 254.
            int bad = data.AsSpan(pos + 8, fieldSize).IndexOfAny((byte)0x00, (byte)0xFF);
            if (bad >= 0)
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "lyrics3.invalid-byte",
                    $"Field {id} contains the byte ${data[pos + 8 + bad]:X2}, outside the 01 to 254 that Lyrics3 v2.00 allows.",
                    field));
                confidence = Confidence.Low;
            }

            if (id == "IND")
            {
                if (pos != Begin.Length)
                {
                    findings.Add(new Finding(Severity.Warning, "lyrics3.ind-not-first", "The IND field must be the first field when it is present.", field));
                    confidence = confidence == Confidence.High ? Confidence.Medium : confidence;
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

        if (confidence == Confidence.High)
        {
            evidence.Add(new Evidence("Fields end exactly at the size field", start, size));
        }

        return TagProbe.Accept(tag, marker, confidence, evidence, findings);
    }

    private static string Bool(string indications, int index) =>
        index < indications.Length && indications[index] == '1' ? "true" : "false";
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    // Lyrics3 v2.00 field table: these fields hold at most 250 characters.
    private static readonly HashSet<string> ShortFields = ["AUT", "EAL", "EAR", "ETT"];

    // "Timestamp format is [mm:ss] (no spaces allowed in the timestamps)."
    private static readonly Regex Timestamp = new(@"\[([0-9]{2}):([0-9]{2})\]", RegexOptions.CultureInvariant);

    public string Name => "Lyrics3";

    public string? MustPrecede => "ID3v1";

    public TagProbe? ProbeBefore(TagProbeContext context, long end)
    {
        ByteReader reader = context.Reader;
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
    // 5100 bytes. Lyrics3 v1 says to "seek back 5100 bytes and then search forward for the begin
    // tag", and lyrics may mention LYRICSBEGIN themselves, so the earliest one after which only
    // text follows starts the tag; audio before the tag holds $FF bytes. The search does not run
    // back past an end marker either: "LYRICSBEGIN" and "LYRICSEND" must not be present in the
    // lyrics, so an earlier LYRICSEND or LYRICS200 ends an earlier tag, and text tags such as a
    // space-padded ID3v1 tag can sit between the two. Without a LYRICSBEGIN in that range, the
    // nearest one starts the tag.
    private TagProbe ParseV1(ByteReader reader, long end, long marker)
    {
        long windowStart = Math.Max(reader.Start, marker - MaxV1Search - Begin.Length);
        byte[] window = reader.ReadBytesAt(windowStart, (int)(marker - windowStart));
        ReadOnlySpan<byte> span = window;
        int nearest = span.LastIndexOf(Begin);
        if (nearest < 0)
        {
            return TagProbe.Reject(marker, "there is no LYRICSBEGIN before LYRICSEND");
        }

        int bound = span.LastIndexOfAny((byte)0x00, (byte)0xFF) + 1;
        bound = Math.Max(bound, EndOf(span, EndV1));
        bound = Math.Max(bound, EndOf(span, EndV2));
        int first = span[bound..].IndexOf(Begin);
        int index = first >= 0 ? bound + first : nearest;

        long start = windowStart + index;
        long lyricsStart = start + Begin.Length;
        long lyricsLength = marker - lyricsStart;
        ReadOnlySpan<byte> lyricsBytes = span[(index + Begin.Length)..];
        var tag = new Node(Name, start, end - start, "1.00") { Kind = NodeKind.Tag, Description = Explain.V1Tag, Topic = Topics.Lyrics3 };
        tag.Add("Begin", start, Begin.Length, "LYRICSBEGIN", Explain.Begin);
        string lyrics = Encoding.Latin1.GetString(lyricsBytes);
        Node lyricsNode = tag.Add("Lyrics", lyricsStart, lyricsLength, lyrics, Explain.Lyrics);
        tag.Add("End", marker, MarkerSize, "LYRICSEND", Explain.V1End);

        var findings = new List<Finding>();
        var evidence = new List<Evidence>
        {
            new("End marker \"LYRICSEND\"", marker, MarkerSize),
            new($"\"LYRICSBEGIN\" {Counts.Of(marker - start, "byte", "bytes")} earlier", start, Begin.Length),
        };
        Confidence confidence = Confidence.High;

        if (lyricsLength > MaxV1Lyrics)
        {
            findings.Add(new Finding(Severity.Warning, "lyrics3.too-long", $"The lyrics are {Counts.Of(lyricsLength, "byte", "bytes")} long; Lyrics3 v1 allows at most 5100.", lyricsNode));
        }

        // Lyrics3 v1: "A byte in the text must not have the binary value 255." $00 is reported as
        // well: v1 does not mention it, but its successor v2.00 restricts text to 01 to 254, and
        // zero bytes are what padding and binary data look like, not lyrics.
        int bad = lyricsBytes.IndexOfAny((byte)0x00, (byte)0xFF);
        if (bad >= 0)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "lyrics3.invalid-byte",
                $"The lyrics contain the byte ${lyricsBytes[bad]:X2} at 0x{lyricsStart + bad:X}, which is not lyrics text.",
                lyricsNode));
            confidence = Confidence.Low;
        }

        // Lyrics3 v1: "The keywords "LYRICSBEGIN" and "LYRICSEND" must not be present in the lyrics."
        int begin = lyricsBytes.IndexOf(Begin);
        if (begin >= 0)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "lyrics3.begin-in-lyrics",
                $"The lyrics contain \"LYRICSBEGIN\" at 0x{lyricsStart + begin:X}, which Lyrics3 v1 does not allow; the tag starts at the first one, at 0x{start:X}.",
                lyricsNode));
        }

        int endMarker = lyricsBytes.IndexOf(EndV1);
        if (endMarker >= 0)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "lyrics3.end-in-lyrics",
                $"The lyrics contain \"LYRICSEND\" at 0x{lyricsStart + endMarker:X}, which Lyrics3 v1 does not allow.",
                lyricsNode));
        }

        return TagProbe.Accept(tag, marker, confidence, evidence, findings);
    }

    // The offset just past the last occurrence of value in data, or 0.
    private static int EndOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value)
    {
        int index = data.LastIndexOf(value);
        return index < 0 ? 0 : index + value.Length;
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
        var tag = new Node(Name, start, end - start, "2.00") { Kind = NodeKind.Tag, Description = Explain.V2Tag, Topic = Topics.Lyrics3 };
        var findings = new List<Finding>();
        tag.Add("Begin", start, Begin.Length, "LYRICSBEGIN", Explain.Begin);

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

        (string Value, Node Node)? indications = null;
        bool hasLyricsField = false;
        bool hasTimestamps = false;

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
                Node unparsed = tag.Add("Unparsed", start + pos, data.Length - pos, NodeValue.Integer(data.Length - pos, "bytes"), Explain.Unparsed);
                findings.Add(new Finding(Severity.Warning, "lyrics3.fields-invalid", $"{problem} The rest of the fields is not parsed.", unparsed));
                confidence = Confidence.Low;
                break;
            }

            long fieldStart = start + pos;
            string value = Encoding.Latin1.GetString(data, pos + 8, fieldSize);
            Node field = tag.Add(id, fieldStart, 8 + fieldSize, value, Explain.Field(id));
            field.Add("ID", fieldStart, 3, id, Explain.FieldId);
            field.Add("Size", fieldStart + 3, 5, NodeValue.Integer(fieldSize), Explain.FieldSize);
            Node dataNode = field.Add("Data", fieldStart + 8, fieldSize, value, Explain.FieldData);

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
                // "This is always three (3) characters big in v2.00."
                string[] indicators = ["HasLyrics", "HasTimestamps", "IsRandomSelectionInhibited"];
                string[] texts = [Explain.HasLyrics, Explain.HasTimestamps, Explain.IsRandomSelectionInhibited];
                for (int i = 0; i < Math.Min(fieldSize, indicators.Length); i++)
                {
                    field.Add(indicators[i], fieldStart + 8 + i, 1, NodeValue.Boolean(IsSet(value, i)), texts[i]);
                }

                // The 1998 Lyrics3 v2.00 text defines 2 characters, id3.org's later one 3.
                if (fieldSize is not (2 or 3))
                {
                    findings.Add(new Finding(Severity.Warning, "lyrics3.ind-size", $"The IND field holds {Counts.Of(fieldSize, "character", "characters")}; Lyrics3 v2.00 defines 2, and a later revision 3.", field));
                }

                indications = (value, field);
            }
            else if (!KnownFields.Contains(id))
            {
                findings.Add(new Finding(Severity.Warning, "lyrics3.unknown-field", $"Lyrics3 field ID {id} is not defined by Lyrics3 v2.00.", field));
            }
            else if (id == "LYR")
            {
                hasLyricsField = true;
                hasTimestamps |= AddLyricsLines(dataNode, value, findings);
            }
            else if (id == "IMG")
            {
                AddImageLines(dataNode, value, findings);
            }

            if (ShortFields.Contains(id) && fieldSize > 250)
            {
                findings.Add(new Finding(Severity.Warning, "lyrics3.field-too-long", $"Field {id} holds {Counts.Of(fieldSize, "character", "characters")}; Lyrics3 v2.00 allows at most 250.", field));
            }

            pos += 8 + fieldSize;
        }

        if (indications is ({ } flags, { } indNode))
        {
            if (IsSet(flags, 0) != hasLyricsField)
            {
                findings.Add(new Finding(Severity.Warning, "lyrics3.ind-mismatch", $"IND says the tag {(hasLyricsField ? "has no" : "has a")} LYR field, but it {(hasLyricsField ? "has one" : "has none")}.", indNode));
            }

            if (IsSet(flags, 1) != hasTimestamps)
            {
                findings.Add(new Finding(Severity.Warning, "lyrics3.ind-mismatch", $"IND says the lyrics {(hasTimestamps ? "have no" : "have")} timestamps, but they {(hasTimestamps ? "have some" : "have none")}.", indNode));
            }
        }

        tag.Add("Size", sizeStart, SizeFieldLength, NodeValue.Integer(size), Explain.V2Size);
        tag.Add("End", marker, MarkerSize, "LYRICS200", Explain.V2End);

        if (confidence == Confidence.High)
        {
            evidence.Add(new Evidence("Fields end exactly at the size field", start, size));
        }

        return TagProbe.Accept(tag, marker, confidence, evidence, findings);
    }

    // "Fields that include more then one line uses [CR][LF] delimiters between lines." Returns
    // whether any line carries a timestamp, which may appear "anywhere in the text in any order".
    private static bool AddLyricsLines(Node data, string text, List<Finding> findings)
    {
        bool any = false;
        foreach ((int start, int length) in Lines(text))
        {
            string line = text.Substring(start, length);
            Node lineNode = data.Add("Line", data.Offset + start, length, line, Explain.Line);
            foreach (Match match in Timestamp.Matches(line))
            {
                any = true;
                Node stamp = lineNode.Add("Timestamp", lineNode.Offset + match.Index, match.Length, $"{match.Groups[1].Value}:{match.Groups[2].Value}", Explain.LineTimestamp);
                CheckSeconds(match, stamp, findings);
            }
        }

        return any;
    }

    // "Image lines include filename, description and timestamp separated by delimiter - two
    // ASCII chars 124 ("||"). Description and timestamp are optional", one image per line.
    private static void AddImageLines(Node data, string text, List<Finding> findings)
    {
        foreach ((int start, int length) in Lines(text))
        {
            string line = text.Substring(start, length);
            Node image = data.Add("Image", data.Offset + start, length, line, Explain.Image);
            string[] parts = line.Split("||");
            if (parts.Length > 3 || parts[0].Length == 0)
            {
                findings.Add(new Finding(Severity.Warning, "lyrics3.image-invalid", "An IMG line is not \"filename||description||timestamp\".", image));
            }

            string[] names = ["Filename", "Description", "Timestamp"];
            string[] texts = [Explain.ImageFilename, Explain.ImageDescription, Explain.ImageTimestamp];
            int offset = 0;
            for (int i = 0; i < Math.Min(parts.Length, names.Length); i++)
            {
                Node part = image.Add(names[i], image.Offset + offset, parts[i].Length, parts[i], texts[i]);
                if (i == 1 && parts[i].Length > 250)
                {
                    findings.Add(new Finding(Severity.Warning, "lyrics3.field-too-long", $"An IMG description holds {Counts.Of(parts[i].Length, "character", "characters")}; Lyrics3 v2.00 allows at most 250.", part));
                }
                else if (i == 2 && parts[i].Length > 0)
                {
                    Match match = Timestamp.Match(parts[i]);
                    if (!match.Success || match.Length != parts[i].Length)
                    {
                        findings.Add(new Finding(Severity.Warning, "lyrics3.timestamp-invalid", $"The IMG timestamp \"{parts[i]}\" is not [mm:ss].", part));
                    }
                    else
                    {
                        CheckSeconds(match, part, findings);
                    }
                }

                offset += parts[i].Length + 2;
            }
        }
    }

    private static void CheckSeconds(Match match, Node node, List<Finding> findings)
    {
        if (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) >= 60)
        {
            findings.Add(new Finding(Severity.Warning, "lyrics3.timestamp-invalid", $"The timestamp {match.Value} has more than 59 seconds.", node));
        }
    }

    private static IEnumerable<(int Start, int Length)> Lines(string text)
    {
        int start = 0;
        while (true)
        {
            int end = text.IndexOf("\r\n", start, StringComparison.Ordinal);
            if (end < 0)
            {
                yield return (start, text.Length - start);
                yield break;
            }

            yield return (start, end - start);
            start = end + 2;
        }
    }

    private static bool IsSet(string indications, int index) => index < indications.Length && indications[index] == '1';
}

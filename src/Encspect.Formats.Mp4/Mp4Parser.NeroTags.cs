using Encspect.Core;

namespace Encspect.Formats.Mp4;

// Nero's tags box: Nero's encoders and neroAacTag write a `tags` box into the movie's user data, beside the iTunes metadata. No specification is known; the
// layout is read from the bytes of the committed files that hold it (neroAacEnc 1.0.0.2 to 1.3.3.0, Nero Burning ROM's ndaudio 1.7.27.0 and neroAacTag
// 1.5.4.0: tests/fixtures/aac/sample-kit/external-nero*), checked against neroAacTag's own listings for the last. The box holds a field list (`meta`: a 32-bit count, then per field a flags byte, a 16-bit key length, the key, a 16-bit value
// type, a 32-bit value length and the value), covers (`cvrx`) and segments (`tseg`: a `tshd` header with the segment's number and, in the file with chapters,
// a field list of the chapter's own).
public sealed partial class Mp4Parser
{
    private const string NeroTagsName = "Nero Tags";

    // The fields kept for the comparison with the iTunes metadata, at most.
    private const int MaxNeroFieldsKept = 10_000;

    // The iTunes item whose text a field of Nero's tag repeats, by the field's key; other keys are compared with the freeform item of the same name.
    private static string ItunesKeyOf(string key) => key switch
    {
        "title" => "©nam",
        "artist" => "©ART",
        "album" => "©alb",
        "comment" => "©cmt",
        "tool" => "©too",
        "album artist" => "aART",
        "composer" => "©wrt",
        "genre" => "©gen",
        "year" => "©day",
        _ => key,
    };

    // A tags box of the movie's user data whose first box is one of the three that Nero's tag holds; any other box of that type stays unread.
    private static bool IsNeroTags(State state, BoxHeader header) =>
        state.ParentType == "udta" && ReadHeader(state.Reader, header.PayloadStart, header.End) is { Type: "meta" or "cvrx" or "tseg" };

    private static BoxName NeroNaming(State state, BoxHeader header) => header.Type switch
    {
        "meta" => new("Nero Tag Fields", NodeKind.Detail, Explain.NeroFields, Topics.NeroTags),
        "cvrx" => new("Nero Cover", NodeKind.Detail, Explain.NeroCover, Topics.NeroTags),
        "tseg" => new("Nero Tag Segment", NodeKind.Detail, Explain.NeroSegment, Topics.NeroTags),
        "tshd" => new("Nero Tag Segment Header", NodeKind.Detail, Explain.NeroSegmentHeader, Topics.NeroTags),
        _ => Naming(state, header),
    };

    private static void NeroTags(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        int before = state.NeroFieldCount;
        int? outerSegment = state.NeroSegment;
        state.NeroSegment = null;
        try
        {
            ReadChildren(state, node, f.Position, header.End, depth + 1, custom: NeroChild, naming: h => NeroNaming(state, h));
        }
        finally
        {
            state.NeroSegment = outerSegment;
        }

        node.Value = NodeValue.Integer(state.NeroFieldCount - before, "fields", "field");
    }

    private static void NeroChild(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        switch (header.Type)
        {
            case "meta":
                NeroFieldList(state, node, header, f);
                break;
            case "cvrx":
                NeroCover(state, node, f);
                break;
            case "tseg":
                // The header may follow the fields, so the segment's fields take its number once it is read; without one they are no one's.
                int? outer = state.NeroSegment;
                int firstField = state.NeroFields.Count;
                state.NeroSegment = -1;
                int number;
                try
                {
                    ReadChildren(state, node, f.Position, header.End, depth + 1, custom: NeroChild, naming: h => NeroNaming(state, h));
                }
                finally
                {
                    number = state.NeroSegment ?? -1;
                    state.NeroSegment = outer;
                }

                // Fields of a segment inside this one have their own number already.
                for (int i = firstField; i < state.NeroFields.Count; i++)
                {
                    if (state.NeroFields[i].Segment == -1)
                    {
                        state.NeroFields[i].Segment = number;
                    }
                }

                break;
            case "tshd":
                int index = (int)Math.Min(f.U32(node, "Segment Index", Explain.NeroSegmentIndex), int.MaxValue);
                node.Value = NodeValue.Integer(index);
                if (state.NeroSegment is not null)
                {
                    state.NeroSegment = index;
                }

                break;
            default:
                if (f.Remaining > 0)
                {
                    node.Add(new Node("Data", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.UnreadData });
                }

                break;
        }
    }

    private static void NeroFieldList(State state, Node node, BoxHeader header, BoxFields f)
    {
        uint count = f.U32(node, "Entries", Explain.NeroFieldCount);
        node.Value = NodeValue.Integer(count, "fields", "field");
        int read = 0;
        for (; read < count && f.Remaining > 0; read++)
        {
            if ((read & 0x3FF) == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (read >= MaxListedEntries)
            {
                node.Add(new Node("More Fields", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.NeroMoreFields });
                f.Skip(f.Remaining);
                return;
            }

            long at = f.Position;
            byte flags = (byte)f.Take(1);
            int keyLength = (int)f.Take(2);
            byte[] keyBytes = f.TakeBytes(keyLength);
            string key = Latin1OrUtf8(keyBytes);
            ushort type = (ushort)f.Take(2);
            long length = (long)f.Take(4);
            bool fits = length <= f.Remaining;
            long valueLength = fits ? length : f.Remaining;
            long valueAt = f.Position;
            byte[] bytes = f.TakeBytes(valueLength);
            NodeValue value = type == 1 ? Latin1OrUtf8(bytes) : bytes.Length <= 32 ? $"0x{Convert.ToHexString(bytes)}" : NodeValue.Integer(bytes.Length, "bytes");
            string itunes = ItunesKeyOf(key);
            string name = itunes != key ? BoxNames.Item(itunes) ?? key : key;
            var field = new Node(name, at, f.Position - at, value) { Description = Explain.NeroField, Topic = Topics.NeroTags };
            field.Add(new Node("Flags", at, 1, NodeValue.Hex(flags, 2)) { Description = Explain.NeroFieldFlags });
            field.Add(new Node("Key Length", at + 1, 2, NodeValue.Integer(keyLength, "bytes")) { Description = Explain.NeroKeyLength });
            field.Add(new Node("Key", at + 3, keyLength, key) { Description = Explain.NeroKey });
            field.Add(new Node("Value Type", at + 3 + keyLength, 2, NodeValue.Integer(type)) { Description = Explain.NeroValueType });
            field.Add(new Node("Value Length", at + 5 + keyLength, 4, NodeValue.Integer(length, "bytes")) { Description = Explain.NeroValueLength });
            field.Add(new Node("Value", valueAt, valueLength, value) { Description = Explain.NeroValue });
            node.Add(field);
            state.NeroFieldCount++;
            // A cut value is not compared: the size finding says it is cut, and it would differ from the intact copy.
            if (type == 1 && fits && state.NeroFields.Count < MaxNeroFieldsKept)
            {
                state.NeroFields.Add(new NeroField(key, value.AsText()!, field, state.NeroSegment));
            }

            if (!fits)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.nero-field-size-invalid",
                    $"The field {key} at 0x{at:X} of Nero's tags box states {Counts.Of(length, "byte", "bytes")}, but {valueLength} {Counts.Agree(valueLength, "follows", "follow")} in its list; the value is cut there.", field);
                return;
            }
        }

        if (read < count || f.Remaining > 0)
        {
            Node rest = f.Remaining > 0 ? node.Add(new Node("Unused Bytes", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.UnusedBytes }) : node;
            state.Findings.AddNow(Severity.Warning, "mp4.nero-field-count-invalid",
                $"The field list at 0x{header.Offset:X} of Nero's tags box states {Counts.Of(count, "field", "fields")}, but {(read < count ? $"holds {read}" : $"{Counts.Of(f.Remaining, "byte", "bytes")} follow the {read}")}.", rest);
            f.Skip(f.Remaining);
        }
    }

    private static void NeroCover(State state, Node node, BoxFields f)
    {
        f.U32(node, "First Value", Explain.NeroCoverFirst);
        f.U32(node, "Second Value", Explain.NeroCoverSecond);
        int typeLength = f.U16(node, "Cover Type Length", Explain.NeroCoverTypeLength);
        node.Value = Latin1OrUtf8(f.Bytes(node, "Cover Type", typeLength, Explain.NeroCoverType, b => Latin1OrUtf8(b)));
        f.U16(node, "Value Type", Explain.NeroValueType);
        uint length = f.U32(node, "Image Length", Explain.NeroImageLength);
        long at = f.Position;
        long available = Math.Min(length, f.Remaining);
        byte[] head = f.TakeBytes(Math.Min(available, 8));
        f.Skip(available - head.Length);
        string format = head switch
        {
            [0xFF, 0xD8, 0xFF, ..] => "JPEG image",
            [0x89, 0x50, 0x4E, 0x47, ..] => "PNG image",
            [0x42, 0x4D, ..] => "BMP image",
            _ => "image data",
        };
        var image = new Node("Image", at, available, $"{format}, {Counts.Of(available, "byte", "bytes")}") { Description = Explain.NeroImage };
        node.Add(image);
        if (length > available)
        {
            // The cut cover keeps its slot (length -1), so that later covers meet their own iTunes images; it is not compared.
            state.NeroCovers.Add((at, -1, image));
            state.Findings.AddNow(Severity.Warning, "mp4.nero-field-size-invalid",
                $"The cover at 0x{node.Offset:X} of Nero's tags box states an image of {Counts.Of(length, "byte", "bytes")}, but {available} {Counts.Agree(available, "follows", "follow")} in its box; the image is cut there.", image);
        }
        else
        {
            state.NeroCovers.Add((at, available, image));
            if (f.Remaining > 0)
            {
                node.Add(new Node("Data", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.UnreadData });
            }
        }
    }

    // The fields and covers of Nero's tag against what the iTunes metadata and the chapter list hold: each is written twice, and a copy that differs says that
    // one of the two was changed after the other was written.
    private static void CompareNeroTags(State state)
    {
        foreach (NeroField field in state.NeroFields)
        {
            if (field.Segment is { } segment)
            {
                if (field.Key == "title" && segment >= 0 && segment < state.ChapterTitles.Count && !SameTitle(state.ChapterTitles[segment], field.Text))
                {
                    string chapter = state.ChapterTitles[segment].Title;
                    state.Findings.Add(Severity.Warning, "mp4.nero-chapter-differs", field.Node, times =>
                        $"{(times == 1 ? $"The title of segment {segment} in Nero's tags box, \"{Shorten(field.Text)}\", differs from that of chapter {segment + 1} in the chpl box, \"{Shorten(chapter)}\"" : $"{times} chapter titles in Nero's tags box differ from those of the chpl box, first segment {segment}")}.");
                }
            }
            // Nero Burning ROM writes composer as a freeform item, not as ©wrt, so the item of the same name is the fallback.
            else if ((state.ItunesText.TryGetValue(ItunesKeyOf(field.Key), out string? item) || state.ItunesText.TryGetValue(field.Key, out item)) && item != field.Text.TrimEnd('\0'))
            {
                state.Findings.Add(Severity.Warning, "mp4.nero-field-differs", field.Node, times =>
                    $"{(times == 1 ? $"The field {field.Key} of Nero's tags box, \"{Shorten(field.Text)}\", differs from the iTunes metadata beside it, \"{Shorten(item)}\"" : $"{times} fields of Nero's tags box differ from the iTunes metadata, first {field.Key}")}.");
            }
        }

        int covers = Math.Min(state.NeroCovers.Count, state.ItunesCovers.Count);
        for (int i = 0; i < covers; i++)
        {
            (long Offset, long Length, Node Node) nero = state.NeroCovers[i];
            (long Offset, long Length) itunes = state.ItunesCovers[i];
            if (nero.Length < 0)
            {
                continue;
            }

            if (nero.Length != itunes.Length || !SameBytes(state, nero.Offset, itunes.Offset, nero.Length))
            {
                state.Findings.Add(Severity.Warning, "mp4.nero-cover-differs", nero.Node, times =>
                    $"{(times == 1 ? $"The cover image of Nero's tags box ({Counts.Of(nero.Length, "byte", "bytes")}) differs from the iTunes metadata's ({Counts.Of(itunes.Length, "byte", "bytes")})" : $"{times} cover images of Nero's tags box differ from the iTunes metadata's")}.");
            }
        }
    }

    // A chpl title holds at most 255 bytes, so a longer title of Nero's is the same where the chpl title is its beginning.
    // Compared as bytes, since the cut may fall inside a UTF-8 character.
    private static bool SameTitle((string Title, byte[] Raw) chapter, string title) =>
        chapter.Title == title || (chapter.Raw.Length == 255 && System.Text.Encoding.UTF8.GetBytes(title).AsSpan().StartsWith(chapter.Raw));

    private static string Shorten(string text) => text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 57), "...");

    private static bool SameBytes(State state, long first, long second, long length)
    {
        byte[] a = new byte[(int)Math.Min(length, 65536)];
        byte[] b = new byte[a.Length];
        for (long done = 0; done < length; done += a.Length)
        {
            state.Context.CancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(a.Length, length - done);
            state.Reader.Position = first + done;
            state.Reader.Read(a.AsSpan(0, count));
            state.Reader.Position = second + done;
            state.Reader.Read(b.AsSpan(0, count));
            if (!a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count)))
            {
                return false;
            }
        }

        return true;
    }
}

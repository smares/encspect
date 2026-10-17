using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// QuickTime File Format, "User data atoms": an atom of a udta box whose type begins with © is international text, a list of text items
// that each hold a 16-bit size, a 16-bit language code and the text. A language code below 0x400 is a Macintosh language code and says
// the text is Macintosh-encoded; else it is three letters of ISO 639-2/T and the text is UTF-8, or UTF-16 after a byte order mark.
public sealed partial class Mp4Parser
{
    // The code page of the Macintosh Roman encoding (the Macintosh-encoded text of the languages that are written with it).
    private static readonly Encoding? MacRoman = CodePagesEncodingProvider.Instance.GetEncoding(10000);

    private static bool QuickTimeText(State state, Node node, BoxHeader header, BoxFields f)
    {
        // An atom that the specification's table does not list is read as text only where its items fill it exactly.
        if (BoxNames.UserDataText(header.Type) is null && !FillsWithTextItems(state.Reader, f.Position, f.End))
        {
            return false;
        }

        string? first = null;
        int count = 0;
        long moreStart = f.Position;
        while (f.Remaining > 0)
        {
            if ((count & 0x3FF) == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            long at = f.Position;
            if (f.Remaining < 4)
            {
                Node rest = node.Add(new Node("Unused Bytes", at, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.TextItemTrailing });
                state.Findings.Add(Severity.Warning, "mp4.text-item-truncated", rest, times =>
                    $"{(times == 1 ? $"The {header.Type} atom at 0x{header.Offset:X} ends" : $"{times} text atoms end")} with fewer than the 4 bytes of a text item's size and language{(times == 1 ? string.Empty : $", first the {header.Type} atom at 0x{header.Offset:X}")}.");
                break;
            }

            int size = (int)f.Take(2);
            uint language = (uint)f.Take(2);
            bool fits = size <= f.Remaining;
            byte[] bytes = f.TakeBytes(fits ? size : f.Remaining);
            count++;
            string? text = null;
            if (count <= MaxListedEntries || first is null)
            {
                text = DecodeText(bytes, language);
                if (first is null && text.Length > 0)
                {
                    first = text;
                }
            }

            Node? shown = null;
            if (count <= MaxListedEntries)
            {
                shown = node.Add(new Node("Text Item", at, 4 + bytes.Length, text) { Description = Explain.TextItem });
                shown.Add(new Node("Text Length", at, 2, NodeValue.Integer(size, "bytes")) { Description = Explain.TextLength });
                shown.Add(new Node("Language", at + 2, 2, LanguageValue(language)) { Description = Explain.TextLanguage });
                shown.Add(new Node("Text", at + 4, bytes.Length, text) { Description = Explain.Text });
                moreStart = f.Position;
            }

            if (!fits)
            {
                state.Findings.Add(Severity.Warning, "mp4.text-size-invalid", shown ?? node, times =>
                    $"{(times == 1 ? $"The text item at 0x{at:X} of the {header.Type} atom states {size} bytes, but {bytes.Length} follow" : $"{times} text items state more bytes than their atom holds, first the item at 0x{at:X} of the {header.Type} atom at 0x{header.Offset:X}")}; the text is cut there.");
                break;
            }
        }

        if (count > MaxListedEntries)
        {
            node.Add(new Node("More Text Items", moreStart, f.Position - moreStart, NodeValue.Integer(count - MaxListedEntries, "items", "item")) { Description = Explain.MoreTextItems });
        }

        node.Value = first ?? (count > 0 ? string.Empty : null);
        // ©swr is the "name and version number of the software (or hardware) that generated this movie" (QuickTime File Format, user data
        // atoms), and no Apple specification defines ©too (ffmpeg writes its muxer version there): both are the writer's, unless the text is
        // a libavcodec encoder ID, which names the encoder. As in an item list, the first text counts.
        if (header.Type is "©swr" or "©too" && first is not null)
        {
            state.Context.AddEncoderClaim(new EncoderClaim(first, state.Findings.Resolve(node), $"QuickTime {header.Type} atom", ClaimRoles.Of(ClaimRole.Writer, first)));
        }

        return true;
    }

    // Whether the payload is a run of text items whose sizes end exactly at its end (checked for the first items only).
    private static bool FillsWithTextItems(ByteReader reader, long start, long end)
    {
        long position = start;
        for (int i = 0; i < MaxListedEntries; i++)
        {
            if (end - position < 4)
            {
                return false;
            }

            byte[] size = reader.ReadBytesAt(position, 2);
            position += 4 + ((size[0] << 8) | size[1]);
            if (position == end)
            {
                return true;
            }

            if (position > end)
            {
                return false;
            }
        }

        return true;
    }

    private static string DecodeText(byte[] bytes, uint language)
    {
        if (language < 0x400 || language == 0x7FFF)
        {
            return (MacRoman ?? Encoding.Latin1).GetString(bytes);
        }

        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : Latin1OrUtf8(bytes);
    }

    private static NodeValue LanguageValue(uint code)
    {
        if (code == 0x7FFF)
        {
            return "32767 (unspecified Macintosh language)";
        }

        if (code < 0x400)
        {
            return BoxNames.MacintoshLanguage(code) is { } name ? $"{code} ({name}, Macintosh language code)" : $"{code} (Macintosh language code)";
        }

        uint[] letters = [(code >> 10) & 0x1F, (code >> 5) & 0x1F, code & 0x1F];
        return code < 0x8000 && letters.All(l => l is >= 1 and <= 26)
            ? new string([.. letters.Select(l => (char)(l + 0x60))])
            : $"0x{code.ToString("X4", CultureInfo.InvariantCulture)} (not a packed ISO 639-2/T code)";
    }
}

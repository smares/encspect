using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// QuickTime File Format, "User data atoms": an atom of a udta box whose type begins with © is international text, a list of text items
// that each hold a 16-bit size, a 16-bit language code and the text. A language code below 0x400 is a Macintosh language code and says
// the text is Macintosh-encoded (as does 0x7FFF, an unspecified Macintosh language); any other code is three letters of ISO 639-2/T and the
// text is UTF-8, or UTF-16 after a byte order mark.
public sealed partial class Mp4Parser
{
    // The code page of the Macintosh Roman encoding (the Macintosh-encoded text of the languages that are written with it).
    private static readonly Encoding? MacRoman = CodePagesEncodingProvider.Instance.GetEncoding(10000);

    // The text items of all atoms of a file that are listed as nodes: past it, the items are read for the first text and the findings only.
    private const int MaxListedTextItems = 10_000;

    private static bool QuickTimeText(State state, Node node, BoxHeader header, BoxFields f)
    {
        // An atom that the specification's table does not list is read as text only where its items fill it exactly.
        if (BoxNames.UserDataText(header.Type) is null && !FillsWithTextItems(state, f.Position, f.End))
        {
            return false;
        }

        // The items of a box that is not shown (the tree is over its node budget) are walked and not built.
        bool isShown = !state.Findings.IsDetaching;
        string? first = null;
        int count = 0;
        int listed = 0;
        long moreStart = f.Position;
        long tail = -1;
        while (f.Remaining > 0)
        {
            if ((count & 0x3FF) == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            long at = f.Position;
            if (f.Remaining < 4)
            {
                tail = at;
                break;
            }

            int size = (int)f.Take(2);
            uint language = (uint)f.Take(2);
            bool fits = size <= f.Remaining;
            long length = fits ? size : f.Remaining;
            count++;
            bool isListed = isShown && listed < MaxListedEntries && state.TextItemsListed < MaxListedTextItems;
            string? text = null;
            byte[]? bytes = null;
            if (length == 0)
            {
                text = string.Empty;
            }
            else if (isListed || first is null)
            {
                bytes = f.TakeBytes(length);
                text = DecodeText(bytes, language);
                if (first is null && text.Length > 0)
                {
                    first = text;
                }
            }
            else
            {
                f.Skip(length);
            }

            Node? shown = null;
            if (isListed)
            {
                listed++;
                state.TextItemsListed++;
                shown = node.Add(new Node("Text Item", at, 4 + length, text) { Description = Explain.TextItem });
                shown.Add(new Node("Text Length", at, 2, NodeValue.Integer(size, "bytes")) { Description = Explain.TextLength });
                shown.Add(new Node("Language", at + 2, 2, LanguageValue(language)) { Description = Explain.TextLanguage });
                shown.Add(new Node("Text", at + 4, length, text) { Description = Explain.Text });
                moreStart = f.Position;
            }

            if (!fits)
            {
                ReportItemSize(state, shown ?? node, header, at, size, length);
                break;
            }
        }

        if (isShown && count > listed)
        {
            node.Add(new Node("More Text Items", moreStart, f.Position - moreStart, NodeValue.Integer(count - listed, "items", "item")) { Description = Explain.MoreTextItems });
        }

        if (tail >= 0)
        {
            Node rest = isShown ? node.Add(new Node("Unused Bytes", tail, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.TextItemTrailing }) : node;
            ReportItemTail(state, rest, header);
        }

        node.Value = first ?? (count > 0 ? string.Empty : null);
        // ©swr is the "name and version number of the software (or hardware) that generated this movie" (QuickTime File Format, user data
        // atoms), and no Apple specification defines ©too (ffmpeg writes its muxer version into an item list): both are the writer's, unless
        // the text is a libavcodec encoder ID, which names the encoder. The items of an atom are translations of one text, so the first
        // non-empty one is the claim.
        if (header.Type is "©swr" or "©too" && first is not null)
        {
            state.Context.AddEncoderClaim(new EncoderClaim(first, state.Findings.Resolve(node), $"QuickTime {header.Type} atom", ClaimRoles.Of(ClaimRole.Writer, first)));
        }

        return true;
    }

    // Kept out of the item loop, where a closure over its variables would be allocated for every item.
    private static void ReportItemSize(State state, Node at, BoxHeader header, long position, int size, long length) =>
        state.Findings.Add(Severity.Warning, "mp4.text-size-invalid", at, times =>
            $"{(times == 1 ? $"The text item at 0x{position:X} of the {header.Type} atom states {Counts.Bytes(size)}, but {length} {(length == 1 ? "follows" : "follow")}" : $"{times} text items state more bytes than their atom holds, first the item at 0x{position:X} of the {header.Type} atom at 0x{header.Offset:X}")}; the text is cut there.");

    private static void ReportItemTail(State state, Node rest, BoxHeader header) =>
        state.Findings.Add(Severity.Warning, "mp4.text-item-truncated", rest, times =>
            $"{(times == 1 ? $"The {header.Type} atom at 0x{header.Offset:X} ends" : $"{times} text atoms end")} with fewer than the 4 bytes of a text item's size and language{(times == 1 ? string.Empty : $", first the {header.Type} atom at 0x{header.Offset:X}")}.");

    // Whether the payload is a run of text items whose sizes end exactly at its end: every size is read, through a window of the payload.
    private static bool FillsWithTextItems(State state, long start, long end)
    {
        if (end - start < 4)
        {
            return false;
        }

        byte[] window = new byte[(int)Math.Min(65536, end - start)];
        long windowStart = 0;
        int windowLength = 0;
        long position = start;
        for (int items = 0; position < end; items++)
        {
            if ((items & 0xFFF) == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (end - position < 4)
            {
                return false;
            }

            if (position < windowStart || position + 2 > windowStart + windowLength)
            {
                windowStart = position;
                windowLength = (int)Math.Min(window.Length, end - position);
                state.Reader.Position = position;
                state.Reader.Read(window.AsSpan(0, windowLength));
            }

            position += 4 + ((window[position - windowStart] << 8) | window[position - windowStart + 1]);
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

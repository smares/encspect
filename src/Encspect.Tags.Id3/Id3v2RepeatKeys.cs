using System.Security.Cryptography;

namespace Encspect.Tags.Id3;

// What a frame that may repeat must not share with another frame of its kind: "There may be
// more than one "TXXX" frame in each tag, but only one with the same description" and its
// counterparts in ID3v2.2 section 4, ID3v2.3 section 4 and the ID3v2.4 frames document.
internal static class Id3v2RepeatKeys
{
    // Field names the shared field, Shown its value for a message (null when the field is the
    // frame's whole content) and Key what is compared, kind and field included.
    // isWhole is false when the decode limit cut data short, which leaves the contents unknown.
    public static List<(string Field, string? Shown, string Key)> For(int major, string id, ReadOnlyMemory<byte> data, bool isWhole)
    {
        string kind = major == 2
            ? id switch
            {
                "TXX" => "TXXX",
                "WXX" => "WXXX",
                "COM" => "COMM",
                "ULT" => "USLT",
                "SLT" => "SYLT",
                "PIC" => "PIC",
                "GEO" => "GEOB",
                "UFI" => "UFID",
                "POP" => "POPM",
                "CRA" => "AENC",
                "CRM" => "CRM",
                "LNK" => "LINK",
                _ => string.Empty,
            }
            : id;

        var keys = new List<(string, string?, string)>();
        void Add(string field, string? shown, string value) => keys.Add((field, shown, $"{kind}\n{field}\n{value}"));

        void AddDescription(string? description)
        {
            if (description is not null)
            {
                Add("description", Quote(description), description);
            }
        }

        var reader = new Reader(data);
        switch (kind)
        {
            case "TXXX" or "WXXX":
                AddDescription(reader.Encoding() is { } e1 ? reader.Text(e1) : null);
                break;
            case "APIC":
                AddDescription(reader.Encoding() is { } e2 && reader.Text(0) is not null && reader.Fixed(1) is not null ? reader.Text(e2) : null);
                break;
            case "PIC":
                AddDescription(reader.Encoding() is { } e3 && reader.Fixed(4) is not null ? reader.Text(e3) : null);
                break;
            case "GEOB":
                AddDescription(reader.Encoding() is { } e4 && reader.Text(0) is not null && reader.Text(e4) is not null ? reader.Text(e4) : null);
                break;
            case "COMM" or "USLT" or "SYLT":
                // SYLT has its time stamp format and content type between language and descriptor.
                if (reader.Encoding() is { } encoding && reader.Fixed(3) is { } language
                    && (kind != "SYLT" || reader.Fixed(2) is not null) && reader.Text(encoding) is { } description)
                {
                    Add("language and description", $"{language}, {Quote(description)}", $"{language}\n{description}");
                }

                break;
            case "UFID" or "AENC" or "CRM" when reader.Text(0) is { } owner:
                Add("owner", Quote(owner), owner);
                break;
            case "POPM" when reader.Text(0) is { } email:
                Add("email", Quote(email), email);
                break;
            case "RVA2" or "EQU2":
                if ((kind == "RVA2" || reader.Fixed(1) is not null) && reader.Text(0) is { } identification)
                {
                    Add("identification", Quote(identification), identification);
                }

                break;
            case "USER" when reader.Encoding() is not null && reader.Fixed(3) is { } userLanguage:
                Add("language", userLanguage, userLanguage);
                break;
            case "ENCR" or "GRID" when reader.Text(0) is { } registrant:
                Add("owner", Quote(registrant), registrant);
                if (reader.Fixed(1) is { } symbol)
                {
                    string shown = $"${(int)symbol[0]:X2}";
                    Add(kind == "ENCR" ? "method symbol" : "group symbol", shown, shown);
                }

                break;

            // "No two may be identical" holds for COMR and SIGN from ID3v2.4 on.
            case "LINK" or "PRIV" when isWhole:
            case "COMR" or "SIGN" when major == 4 && isWhole:
                Add("contents", null, Convert.ToHexString(SHA256.HashData(data.Span)));
                break;
        }

        return keys;
    }

    private static string Quote(string text) => $"\"{text}\"";

    private sealed class Reader(ReadOnlyMemory<byte> data)
    {
        private int _position;

        public byte? Encoding() => Fixed(1) is { } b ? (byte)b[0] : null;

        public string? Fixed(int length)
        {
            if (_position + length > data.Length)
            {
                return null;
            }

            string text = System.Text.Encoding.Latin1.GetString(data.Span.Slice(_position, length));
            _position += length;
            return text;
        }

        // A terminated string; null when the terminator is missing.
        public string? Text(byte encoding)
        {
            int end = Id3v2Text.FindTerminator(data.Span, _position, data.Length, encoding);
            if (end >= data.Length)
            {
                return null;
            }

            bool? bigEndian = null;
            string text = Id3v2Text.Decode(data.Span, _position, end, encoding, isCut: false, ref bigEndian).Text;
            _position = end + Id3v2Text.TerminatorWidth(encoding);
            return text;
        }
    }
}

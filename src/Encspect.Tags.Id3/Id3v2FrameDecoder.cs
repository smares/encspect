using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Tags.Id3;

// Decodes the content of the frame types this tool knows (ID3v2.3 and 2.4 "native frames",
// ID3v2.2 section 4). Unknown frames are shown as their byte count.
internal sealed class Id3v2FrameDecoder
{
    private static readonly string[] PictureTypes =
    [
        "Other",
        "32x32 pixels file icon (PNG only)",
        "Other file icon",
        "Cover (front)",
        "Cover (back)",
        "Leaflet page",
        "Media (e.g. label side of CD)",
        "Lead artist/lead performer/soloist",
        "Artist/performer",
        "Conductor",
        "Band/Orchestra",
        "Composer",
        "Lyricist/text writer",
        "Recording Location",
        "During recording",
        "During performance",
        "Movie/video screen capture",
        "A bright colored fish",
        "Illustration",
        "Band/artist logotype",
        "Publisher/Studio logotype",
    ];

    private readonly int _major;
    private readonly string _id;
    private readonly Node _frame;
    private readonly MappedBytes _content;
    private readonly long _decodedSize;
    private readonly long _frameEnd;
    private readonly List<Finding> _findings;
    private bool? _bigEndian;
    private bool? _firstBom;
    private bool _hasMixedByteOrder;
    private bool _hasInvalidText;
    private ImageInfo? _image;

    private Id3v2FrameDecoder(int major, string id, Node frame, MappedBytes content, long decodedSize, long frameEnd, List<Finding> findings)
    {
        _frameEnd = frameEnd;
        _major = major;
        _id = id;
        _frame = frame;
        _content = content;
        _decodedSize = decodedSize;
        _findings = findings;
    }

    private ReadOnlySpan<byte> Data => _content.Span;

    private int Length => _content.Length;

    // Content cut short by the decode limit, which a text string may run past.
    private bool IsCut => Length < _decodedSize;

    // content holds the decoded frame data after any extra header bytes, possibly cut short
    // when the frame is very large; decodedSize is its full decoded length and frameEnd the file
    // offset where the frame ends. image is the format and size of an APIC picture.
    public static NodeValue? Decode(int major, string id, Node frame, MappedBytes content, long decodedSize, long frameEnd, List<Finding> findings, out ImageInfo? image)
    {
        var decoder = new Id3v2FrameDecoder(major, id, frame, content, decodedSize, frameEnd, findings);
        NodeValue? value = decoder.Decode();
        image = decoder._image;
        return value;
    }

    private static string Kind(int major, string id)
    {
        if (major == 2)
        {
            id = id switch
            {
                "TXX" => "TXXX",
                "WXX" => "WXXX",
                "COM" => "COMM",
                "ULT" => "USLT",
                "UFI" => "UFID",
                "CNT" => "PCNT",
                "POP" => "POPM",
                "GEO" => "GEOB",
                _ => id,
            };
        }

        return id switch
        {
            "TXXX" or "WXXX" or "COMM" or "USLT" or "APIC" or "PIC" or "UFID" or "PCNT" or "POPM" or "PRIV" or "GEOB" => id,
            _ when id[0] == 'T' => "T",
            _ when id[0] == 'W' => "W",
            _ => "",
        };
    }

    private NodeValue? Decode()
    {
        try
        {
            return Kind(_major, _id) switch
            {
                "T" => (NodeValue)DecodeText(),
                "TXXX" => DecodeUserText(),
                "W" => DecodeUrl(),
                "WXXX" => DecodeUserUrl(),
                "COMM" or "USLT" => DecodeComment(),
                "APIC" => DecodePicture(isV22: false),
                "PIC" => DecodePicture(isV22: true),
                "UFID" => DecodeUniqueFileId(),
                "PRIV" => DecodePrivate(),
                "POPM" => DecodePopularimeter(),
                "PCNT" => DecodeCounter(),
                "GEOB" => DecodeObject(),
                _ => AddBinary("Data", 0, Explain.FrameData),
            };
        }
        catch (TruncatedContentException ex)
        {
            _findings.Add(new Finding(Severity.Warning, "id3v2.frame-content-truncated", $"Frame {_id} ends before its {ex.Field}.", _frame));
            return null;
        }
    }

    private string DecodeText()
    {
        byte encoding = ReadEncoding();
        List<string> values = AddTextValues("Text", 1, encoding, Explain.Text);
        return string.Join("; ", values);
    }

    private string DecodeUserText()
    {
        byte encoding = ReadEncoding();
        (string description, int next) = AddTerminatedText("Description", 1, encoding, Explain.Description);
        List<string> values = AddTextValues("Text", next, encoding, Explain.Text);
        return $"{description}: {string.Join("; ", values)}";
    }

    private string DecodeUrl()
    {
        int end = Id3v2Text.FindTerminator(Data, 0, Length, 0);
        return AddLatin1("URL", 0, end, Explain.Url);
    }

    private string DecodeUserUrl()
    {
        byte encoding = ReadEncoding();
        (string description, int next) = AddTerminatedText("Description", 1, encoding, Explain.Description);
        int end = Id3v2Text.FindTerminator(Data, next, Length, 0);
        string url = AddLatin1("URL", next, end, Explain.Url);
        return $"{description}: {url}";
    }

    private string DecodeComment()
    {
        byte encoding = ReadEncoding();
        Require(4, "language");
        AddLatin1("Language", 1, 4, Explain.Language);
        (string description, int next) = AddTerminatedText("Description", 4, encoding, Explain.Description);
        int end = Id3v2Text.FindTerminator(Data, next, Length, encoding);
        string text = AddText("Text", next, end, encoding, Explain.Text);
        return description.Length > 0 ? $"{description}: {text}" : text;
    }

    private string DecodePicture(bool isV22)
    {
        byte encoding = ReadEncoding();
        int next;
        string format;
        if (isV22)
        {
            Require(4, "image format");
            format = AddLatin1("Image Format", 1, 4, Explain.ImageFormat);
            next = 4;
        }
        else
        {
            int end = Id3v2Text.FindTerminator(Data, 1, Length, 0);
            Require(end + 1, "MIME type");
            format = AddLatin1("MIME Type", 1, end, Explain.MimeType);
            next = end + 1;
        }

        Require(next + 1, "picture type");
        byte type = Data[next];
        string typeName = type < PictureTypes.Length ? PictureTypes[type] : "unknown";
        AddRange("Picture Type", next, next + 1, $"{type} ({typeName})", Explain.PictureType);

        (string description, int dataStart) = AddTerminatedText("Description", next + 1, encoding, Explain.Description);
        AddBinary("Picture Data", dataStart, Explain.PictureData);
        if (!isV22)
        {
            _image = Id3v2Image.Measure(Data, dataStart, Length);
        }

        return description.Length > 0 ? $"{format}, {typeName}, {description}" : $"{format}, {typeName}";
    }

    private string DecodeUniqueFileId()
    {
        (string owner, int next) = AddTerminatedLatin1("Owner", 0, Explain.Owner);
        byte[] identifier = Data[next..].ToArray();
        string value = identifier.All(b => b is >= 0x20 and < 0x7F)
            ? Encoding.ASCII.GetString(identifier)
            : Convert.ToHexString(identifier);
        AddRange("Identifier", next, Length, value, Explain.Identifier);
        return $"{owner}: {value}";
    }

    private string DecodePrivate()
    {
        (string owner, int next) = AddTerminatedLatin1("Owner", 0, Explain.Owner);
        AddBinary("Data", next, Explain.PrivateData);
        return owner;
    }

    private string DecodePopularimeter()
    {
        (string email, int next) = AddTerminatedLatin1("Email", 0, Explain.Email);
        Require(next + 1, "rating");
        AddRange("Rating", next, next + 1, NodeValue.Integer(Data[next]), Explain.Rating);
        if (next + 1 < Length)
        {
            AddRange("Counter", next + 1, Length, FormatCounter(next + 1, Length), Explain.Counter);
        }

        return $"{email}: {Data[next]}";
    }

    private NodeValue DecodeCounter()
    {
        NodeValue value = FormatCounter(0, Length);
        AddRange("Counter", 0, Length, value, Explain.Counter);
        return value;
    }

    private string DecodeObject()
    {
        byte encoding = ReadEncoding();
        (string mime, int next) = AddTerminatedLatin1("MIME Type", 1, Explain.MimeType);
        (string fileName, next) = AddTerminatedText("Filename", next, encoding, Explain.Filename);
        (string description, next) = AddTerminatedText("Description", next, encoding, Explain.Description);
        AddBinary("Object", next, Explain.ObjectData);
        return $"{mime}, {fileName}, {description}";
    }

    private byte ReadEncoding()
    {
        Require(1, "text encoding");
        byte encoding = Data[0];
        AddRange("Encoding", 0, 1, Id3v2Text.EncodingName(encoding), Explain.Encoding, Topics.TextEncodings);

        if (encoding > 3)
        {
            _findings.Add(new Finding(
                Severity.Warning,
                "id3v2.encoding-invalid",
                $"Frame {_id} uses the undefined text encoding {encoding}; its text is shown as ISO-8859-1.",
                _frame));
        }
        else if (encoding > 1 && _major < 4)
        {
            _findings.Add(new Finding(
                Severity.Warning,
                "id3v2.encoding-not-allowed",
                $"Frame {_id} uses {Id3v2Text.EncodingName(encoding)}, which ID3v2.{_major} does not define.",
                _frame));
        }

        return encoding;
    }

    // Text frames hold one string in 2.2 and 2.3 and a terminator-separated list in 2.4.
    private List<string> AddTextValues(string name, int start, byte encoding, string description)
    {
        int width = Id3v2Text.TerminatorWidth(encoding);
        var ranges = new List<(int Start, int End)>();
        int i = start;
        while (i < Length)
        {
            int end = Id3v2Text.FindTerminator(Data, i, Length, encoding);
            ranges.Add((i, end));
            i = end + width;
        }

        while (ranges.Count > 1 && ranges[^1].Start == ranges[^1].End)
        {
            ranges.RemoveAt(ranges.Count - 1);
        }

        if (ranges.Count == 0)
        {
            ranges.Add((Math.Min(start, Length), Math.Min(start, Length)));
        }

        if (_major < 4 && ranges.Count > 1)
        {
            // ID3v2.3 section 4.2 allows it: "all the following information should be ignored".
            _findings.Add(new Finding(
                Severity.Info,
                "id3v2.text-after-terminator",
                $"Frame {_id} has data after its terminated string, which ID3v2.{_major} says to ignore.",
                _frame));
            ranges.RemoveRange(1, ranges.Count - 1);
        }

        var values = new List<string>(ranges.Count);
        foreach ((int s, int e) in ranges)
        {
            values.Add(AddText(name, s, e, encoding, description));
        }

        return values;
    }

    private (string Text, int Next) AddTerminatedText(string name, int start, byte encoding, string description)
    {
        Require(start, name.ToLowerInvariant());
        int end = Id3v2Text.FindTerminator(Data, start, Length, encoding);
        if (end >= Length)
        {
            throw new TruncatedContentException($"{name.ToLowerInvariant()} terminator");
        }

        string text = AddText(name, start, end, encoding, description);
        return (text, end + Id3v2Text.TerminatorWidth(encoding));
    }

    private (string Text, int Next) AddTerminatedLatin1(string name, int start, string description)
    {
        Require(start, name.ToLowerInvariant());
        int end = Id3v2Text.FindTerminator(Data, start, Length, 0);
        if (end >= Length)
        {
            throw new TruncatedContentException($"{name.ToLowerInvariant()} terminator");
        }

        return (AddLatin1(name, start, end, description), end + 1);
    }

    private string AddText(string name, int start, int end, byte encoding, string description)
    {
        bool hasByteOrder = _bigEndian is not null;
        bool isCut = end == Length && IsCut;
        DecodedText decoded = Id3v2Text.Decode(Data, start, end, encoding, isCut, ref _bigEndian);
        string text = decoded.Text;

        if (decoded.MissingBom)
        {
            string assumed = hasByteOrder ? "the earlier byte order is assumed" : "little-endian is assumed";
            if (_major == 2)
            {
                // ID3v2.2 section 3.2 only says Unicode is UCS-2; unlike 2.3 and 2.4 it asks for
                // no byte order mark.
                _findings.Add(new Finding(Severity.Info, "id3v2.utf16-byte-order-assumed", $"Frame {_id} has UCS-2 text without a byte order mark, which ID3v2.2 does not require; {assumed}.", _frame));
            }
            else
            {
                _findings.Add(new Finding(
                    Severity.Warning,
                    "id3v2.utf16-without-bom",
                    hasByteOrder
                        ? $"Frame {_id} has a UTF-16 string without a byte order mark after one with it; {assumed}."
                        : $"Frame {_id} has UTF-16 text without a byte order mark; {assumed}.",
                    _frame));
            }
        }

        // ID3v2.4 section 4: "All strings in the same frame SHALL have the same byteorder."
        if (decoded.Bom is { } bom)
        {
            if (_major == 4 && _firstBom is { } first && first != bom && !_hasMixedByteOrder)
            {
                _hasMixedByteOrder = true;
                _findings.Add(new Finding(
                    Severity.Warning,
                    "id3v2.utf16-byte-order-mixed",
                    $"Frame {_id} has UTF-16 strings in both byte orders; ID3v2.4 requires one byte order per frame.",
                    _frame));
            }

            _firstBom ??= bom;
        }

        if (decoded.IsInvalid && !_hasInvalidText)
        {
            _hasInvalidText = true;
            _findings.Add(new Finding(
                Severity.Warning,
                "id3v2.text-invalid",
                $"Frame {_id} has text that is not valid {Id3v2Text.EncodingName(encoding)}; the bytes that do not decode are shown as U+FFFD.",
                _frame));
        }

        if (isCut)
        {
            long offset = _content.OffsetOf(start);
            _frame.Add(name, offset, _frameEnd - offset, text, description);
            _findings.Add(new Finding(
                Severity.Info,
                "id3v2.frame-text-cut",
                $"Frame {_id} holds {_decodedSize - start} bytes of text; the first {end - start} are decoded and shown, up to the analyzer's limit for one frame.",
                _frame));
            return text;
        }

        AddRange(name, start, end, text, description);
        return text;
    }

    private string AddLatin1(string name, int start, int end, string description)
    {
        string text = Encoding.Latin1.GetString(Data[start..end]);
        AddRange(name, start, end, text, description);
        return text;
    }

    // Binary data runs to the end of the frame, which may lie beyond the decoded prefix.
    private NodeValue AddBinary(string name, int start, string description)
    {
        long size = Math.Max(0, _decodedSize - start);
        long offset = _content.OffsetOf(Math.Min(start, Length));
        NodeValue value = NodeValue.Integer(size, "bytes");
        _frame.Add(name, offset, _frameEnd - offset, value, description);
        return value;
    }

    private void AddRange(string name, int start, int end, NodeValue value, string description, string? topic = null)
    {
        _frame.Add(name, _content.OffsetOf(start), _content.FileLength(start, end), value, description, topic);
    }

    // A counter longer than a long shows its bytes.
    private NodeValue FormatCounter(int start, int end)
    {
        if (end - start > 8)
        {
            return "0x" + Convert.ToHexString(Data[start..end]);
        }

        ulong value = 0;
        for (int i = start; i < end; i++)
        {
            value = (value << 8) | Data[i];
        }

        return value <= long.MaxValue ? NodeValue.Integer((long)value) : value.ToString(CultureInfo.InvariantCulture);
    }

    private void Require(int length, string field)
    {
        if (Length < length)
        {
            throw new TruncatedContentException(field);
        }
    }

    private sealed class TruncatedContentException(string field) : Exception
    {
        public string Field { get; } = field;
    }
}

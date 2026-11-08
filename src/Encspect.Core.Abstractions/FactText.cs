using System.Buffers.Binary;

namespace Encspect.Core;

// The text lossyWAV writes as the body of a fact chunk before the data: "lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard", CR LF and
// two NUL bytes. The WAV parser reads it in the chunk, the FLAC parser in the APPLICATION block with ID "riff" that flac's foreign metadata keeps
// for the chunk, the WavPack parser in the wrapper and the Monkey's Audio parser in the header data; they share this reader because parsers do not
// reference each other. lossyWAV's own check takes a fact chunk before the data whose text starts with "lossyWAV " as its own output, so does this.
public sealed class FactText
{
    public const string Prefix = "lossyWAV ";

    // The most of a fact chunk's body that is read; lossyWAV's text is 53 to 85 bytes.
    public const int MaxLength = 1024;

    // The most chunks a header is walked through for its fact chunk.
    private const int MaxChunks = 64;

    private const string VersionSeparator = " @ ";

    private const string SettingsSeparator = ", ";

    public const string TextDescription = "Text in place of a sample length: lossyWAV writes its version, the date and time of the run and its settings here. It states no number of samples, so none is read from it.";

    public const string VersionDescription = "The program and version that wrote the text, the part before \" @ \".";

    public const string TimeDescription = "The date and time of the run, as lossyWAV wrote them between \" @ \" and the comma. They are shown as written and not read as a date.";

    public const string SettingsDescription = "The options of the run in lossyWAV's long names, the part after the first comma, as written. They are not a reading of what lossyWAV applied.";

    public const string UnrecognizedFinding = "The text of the fact chunk starts with \"lossyWAV \" but is not of the form \"lossyWAV <version> @ <date and time>, <settings>\"; it is shown as written and names no version.";

    private FactText(string text, string? version, string? time, string? settings, int versionAt, int timeAt, int settingsAt)
    {
        Text = text;
        Version = version;
        Time = time;
        Settings = settings;
        VersionAt = versionAt;
        TimeAt = timeAt;
        SettingsAt = settingsAt;
    }

    // The text as written, without the CR LF and NUL bytes that end it.
    public string Text { get; }

    // "lossyWAV 1.4.2": the text up to " @ ". Null with Time and Settings where the text does not have the form.
    public string? Version { get; }

    public string? Time { get; }

    public string? Settings { get; }

    // The positions of the three parts in the body.
    public int VersionAt { get; }

    public int TimeAt { get; }

    public int SettingsAt { get; }

    public bool IsRecognized => Version is not null;

    // Whether a fact chunk's body starts as lossyWAV's text does.
    public static bool StartsLikeText(ReadOnlySpan<byte> body) => body.StartsWith("lossyWAV "u8);

    // Reads the body of a fact chunk that is not known to be lossyWAV's; null where it does not start with "lossyWAV ". The bytes are read as
    // Latin-1, so a position in the text is a position in the body.
    public static FactText? Read(ReadOnlySpan<byte> body)
    {
        if (!StartsLikeText(body))
        {
            return null;
        }

        body = body[..Math.Min(body.Length, MaxLength)];
        int end = body.Length;
        while (end > 0 && body[end - 1] is (byte)'\r' or (byte)'\n' or 0)
        {
            end--;
        }

        var chars = new char[end];
        for (int i = 0; i < end; i++)
        {
            chars[i] = body[i] is < 0x20 or 0x7F ? '�' : (char)body[i];
        }

        string text = new(chars);
        int at = text.IndexOf(VersionSeparator, StringComparison.Ordinal);
        int comma = at < 0 ? -1 : text.IndexOf(SettingsSeparator, at + VersionSeparator.Length, StringComparison.Ordinal);
        if (at < 0 || comma < 0)
        {
            return new FactText(text, null, null, null, 0, 0, 0);
        }

        int timeAt = at + VersionSeparator.Length;
        int settingsAt = comma + SettingsSeparator.Length;
        return new FactText(text, text[..at], text[timeAt..comma], text[settingsAt..], 0, timeAt, settingsAt);
    }

    // The claim of the processor role this text makes: high confidence, as lossyWAV's own check takes the chunk by its prefix alone. Null where the
    // text names no version.
    public EncoderClaim? ToClaim(Node source, string description) =>
        Version is null ? null : new EncoderClaim(Version, source, description, ClaimRole.Processor) { Confidence = Confidence.High, Settings = Settings };

    // The text's node, with the parts as children where it has the form. offset is where the body starts in the file and length how many bytes it has.
    public Node ToNode(long offset, long length)
    {
        var node = new Node("Text", offset, length, Text) { Description = TextDescription, Topic = ClaimRoles.ProcessorTopic };
        if (Version is not null)
        {
            node.Add("Version", offset + VersionAt, Version.Length, Version, VersionDescription, ClaimRoles.ProcessorTopic);
            node.Add("Date and Time", offset + TimeAt, Time!.Length, Time, TimeDescription, ClaimRoles.ProcessorTopic);
            node.Add("Settings", offset + SettingsAt, Settings!.Length, Settings, SettingsDescription, ClaimRoles.ProcessorTopic);
        }

        return node;
    }

    // Finds the first fact chunk before the data in the RIFF chunks that begin at `start` in `chunks` (a header kept apart from the file, such as a
    // WavPack wrapper or Monkey's Audio header data) whose body starts as lossyWAV's text does. bodyAt and bodyLength give it within `chunks`, the
    // length as far as the bytes go.
    public static bool Find(ReadOnlySpan<byte> chunks, int start, out int bodyAt, out int bodyLength)
    {
        bodyAt = 0;
        bodyLength = 0;
        int position = start;
        for (int i = 0; i < MaxChunks && chunks.Length - position >= 8; i++)
        {
            ReadOnlySpan<byte> id = chunks.Slice(position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunks[(position + 4)..]);
            if (id.SequenceEqual("data"u8))
            {
                return false;
            }

            int body = position + 8;
            long held = Math.Min(size, chunks.Length - body);
            if (id.SequenceEqual("fact"u8) && StartsLikeText(chunks.Slice(body, (int)held)))
            {
                bodyAt = body;
                bodyLength = (int)held;
                return true;
            }

            long next = body + (long)size + (size & 1);
            if (next > chunks.Length)
            {
                return false;
            }

            position = (int)next;
        }

        return false;
    }
}

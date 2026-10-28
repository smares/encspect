using System.Buffers.Binary;
using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// What the stored header data holds that the analyzer reads: the kind of file it begins like, and for a RIFF or RF64 WAVE header the few fields
// of its fmt chunk that give the audio's layout, with where each lies. It is not a WAV parser: the chunks before the fmt chunk are skipped by their sizes.
internal sealed record WavFormat
{
    // The reading stopped for this reason, or null where the fmt chunk was read whole.
    public WavProblem Problem { get; init; }

    // What the problem is about: the position of the chunk the header data ends in (or of the position where a chunk should be), its ID and its stated size.
    public long ProblemAt { get; init; }

    public string ProblemChunk { get; init; } = string.Empty;

    public long ProblemSize { get; init; }

    public long ProblemHeld { get; init; }

    public int Tag { get; init; } = -1;

    public long TagAt { get; init; }

    public int Channels { get; init; } = -1;

    public long ChannelsAt { get; init; }

    public uint? Mask { get; init; }

    public long MaskAt { get; init; }

    // The common fields after the channels, read with the mask, which the decoder's AudioFormat states.
    public int SampleRate { get; init; }

    public int BytesPerSecond { get; init; }

    public int BlockAlign { get; init; }

    public int BitsPerSample { get; init; }
}

internal enum WavProblem
{
    None,

    // The header data ends inside a chunk's 8-byte header, or inside a chunk before the fmt chunk.
    CutInChunk,

    // The header data ends inside the fmt chunk.
    CutInFormat,

    // The fmt chunk states fewer bytes than the 16 of the common fields.
    FormatShort,

    // The data chunk or the end of the header data comes before a fmt chunk.
    NoFormat,

    // More chunks than the walk follows.
    TooManyChunks,
}

internal static class WavHeader
{
    // A header that holds more chunks than this before its fmt chunk is not a plausible WAVE header.
    private const int MaxChunks = 64;

    public static string? Container(ByteReader reader, long at, long length)
    {
        if (length < 12 || at + 12 > reader.End)
        {
            return null;
        }

        byte[] head = reader.ReadBytesAt(at, 12);
        ReadOnlySpan<byte> id = head.AsSpan(0, 4);
        ReadOnlySpan<byte> kind = head.AsSpan(8, 4);
        if ((id.SequenceEqual("RIFF"u8) || id.SequenceEqual("RF64"u8)) && kind.SequenceEqual("WAVE"u8))
        {
            return id.SequenceEqual("RF64"u8) ? "an RF64 WAVE header" : "a RIFF WAVE header";
        }

        if (id.SequenceEqual("FORM"u8))
        {
            return "an AIFF header";
        }

        return id.SequenceEqual("caff"u8) ? "a CAF header" : null;
    }

    // Walks the chunks of a RIFF or RF64 WAVE header in the length bytes at `at` to the fmt chunk (RIFFNEW.pdf, "WAVE Format Chunk"; Microsoft,
    // WAVEFORMATEXTENSIBLE) and reads its tag and channels, and for the extensible form with a cbSize of 22 or more the channel mask. Null where the
    // data is no such header. Every read stays within the length bytes.
    public static WavFormat? Read(ByteReader reader, long at, long length)
    {
        long end = Math.Min(at + length, reader.End);
        if (Container(reader, at, end - at) is not ("a RIFF WAVE header" or "an RF64 WAVE header"))
        {
            return null;
        }

        long position = at + 12;
        for (int i = 0; i < MaxChunks; i++)
        {
            if (position >= end)
            {
                return new WavFormat { Problem = WavProblem.NoFormat, ProblemAt = position };
            }

            if (end - position < 8)
            {
                return new WavFormat { Problem = WavProblem.CutInChunk, ProblemAt = position, ProblemSize = -1, ProblemHeld = end - position };
            }

            byte[] head = reader.ReadBytesAt(position, 8);
            string id = System.Text.Encoding.Latin1.GetString(head, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
            long body = position + 8;
            if (id == "data")
            {
                return new WavFormat { Problem = WavProblem.NoFormat, ProblemAt = position };
            }

            if (id == "fmt ")
            {
                return ReadFormat(reader, body, size, end);
            }

            long next = body + size + (size & 1);
            if (body + size > end)
            {
                return new WavFormat { Problem = WavProblem.CutInChunk, ProblemAt = position, ProblemChunk = id, ProblemSize = size, ProblemHeld = end - body };
            }

            position = next;
        }

        return new WavFormat { Problem = WavProblem.TooManyChunks, ProblemAt = position };
    }

    private static WavFormat ReadFormat(ByteReader reader, long body, uint size, long end)
    {
        long held = Math.Min(size, end - body);
        WavProblem problem = size > held ? WavProblem.CutInFormat : size < 16 ? WavProblem.FormatShort : WavProblem.None;
        var format = new WavFormat { Problem = problem, ProblemAt = body - 8, ProblemChunk = "fmt ", ProblemSize = size, ProblemHeld = held };
        if (held < 4)
        {
            return format;
        }

        byte[] fields = reader.ReadBytesAt(body, (int)Math.Min(held, 40));
        format = format with { Tag = BinaryPrimitives.ReadUInt16LittleEndian(fields), TagAt = body, Channels = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(2)), ChannelsAt = body + 2 };
        if (fields.Length >= 24 && format.Tag == WaveExtensible)
        {
            // The cbSize counts the bytes after it, as many as the chunk holds: the mask is read only where the whole extension (valid bits, mask and sub-format) is.
            int extra = (int)Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(16)), held - 18);
            if (extra >= 22)
            {
                format = format with
                {
                    Mask = BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(20)),
                    MaskAt = body + 20,
                    SampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(4)), int.MaxValue),
                    BytesPerSecond = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(8)), int.MaxValue),
                    BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(12)),
                    BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(14)),
                };
            }
        }

        return format;
    }

    public const int WaveExtensible = 0xFFFE;

    public static string TagText(int tag) => tag switch
    {
        1 => $"0x0001 ({CodecNames.Pcm})",
        3 => $"0x0003 ({CodecNames.PcmFloat})",
        WaveExtensible => "0xFFFE (extensible)",
        _ => $"0x{tag:X4}",
    };

    // The nodes of the fields that were read, children of the header data's node, and the findings about what was read.
    public static void AddNodes(State s, WavFormat format, Node data, int channels)
    {
        FindingCollector findings = s.Findings;
        Node? tag = null;
        Node? count = null;
        Node? mask = null;
        if (format.Tag >= 0)
        {
            tag = data.Add("Format Tag", format.TagAt, 2, TagText(format.Tag), Explain.WavFormatTag, Topics.WaveFormat);
            count = data.Add("Channels", format.ChannelsAt, 2, NodeValue.Integer(format.Channels, "channels", "channel"), Explain.WavChannels);
        }

        if (format.Mask is { } value)
        {
            mask = data.Add("Channel Mask", format.MaskAt, 4, NodeValue.Hex(value, 8), Explain.WavChannelMask, Topics.WaveFormatExtensible);
            if (value != 0)
            {
                mask.Add("Speakers", format.MaskAt, 4, ChannelLayouts.Speakers(ChannelLayouts.WaveMaskSpeakers(value)), Explain.WavSpeakers, Topics.Channels);
            }
        }

        switch (format.Problem)
        {
            case WavProblem.CutInChunk when format.ProblemSize < 0:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", data, _ =>
                    $"The WAV header data ends inside a chunk header at 0x{format.ProblemAt:X}, before any fmt chunk, so the channels and the channel mask are not read.", "chunk-header");
                break;
            case WavProblem.CutInChunk:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", data, _ =>
                    $"The WAV header data ends inside the {format.ProblemChunk.Trim()} chunk at 0x{format.ProblemAt:X}, which states {Counts.Of(format.ProblemSize, "byte", "bytes")} and has {format.ProblemHeld} in the header data, before any fmt chunk, so the channels and the channel mask are not read.", "chunk");
                break;
            case WavProblem.CutInFormat:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", tag ?? data, _ =>
                    $"The WAV header data ends inside the fmt chunk at 0x{format.ProblemAt:X}, which states {Counts.Of(format.ProblemSize, "byte", "bytes")} and has {format.ProblemHeld} in the header data; the fields it holds are read as far as they go.", "format-cut");
                break;
            case WavProblem.FormatShort:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", tag ?? data, _ =>
                    $"The fmt chunk at 0x{format.ProblemAt:X} states {Counts.Of(format.ProblemSize, "byte", "bytes")}, fewer than the 16 of the common fields of a WAVE format.", "format-short");
                break;
            case WavProblem.NoFormat:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", data, _ =>
                    $"The WAV header data holds no fmt chunk before its {(format.ProblemAt < data.Offset + data.Length ? "data chunk" : "end")}, so the channels and the channel mask are not read.", "no-format");
                break;
            case WavProblem.TooManyChunks:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", data, _ =>
                    $"The WAV header data holds more than {MaxChunks} chunks before a fmt chunk, which the analyzer does not follow, so the channels and the channel mask are not read.", "chunks");
                break;
        }

        if (count is not null && format.Channels != channels && channels > 0)
        {
            int stated = format.Channels;
            findings.Add(Severity.Warning, "monkeys-audio.wav-channels-differ", count, _ =>
                $"The fmt chunk in the WAV header data states {Counts.Of(stated, "channel", "channels")}, but the Monkey's Audio header states {Counts.Of(channels, "channel", "channels")}; the header data a decoder gives back does not describe the audio it writes.");
        }

        if (mask is not null && format.Mask is { } m && m != 0 && (m & 0x80000000) == 0 && BitOperations.PopCount(m) != channels)
        {
            int positions = BitOperations.PopCount(m);
            bool isMore = positions > channels;
            findings.Add(Severity.Info, "monkeys-audio.channel-mask-mismatch", mask, _ => isMore
                ? $"The channel mask names {Counts.Of(positions, "speaker", "speakers")}, but the header states {Counts.Of(channels, "channel", "channels")}; the extra bits, the most significant, are ignored."
                : $"The channel mask names {Counts.Of(positions, "speaker", "speakers")}, but the header states {Counts.Of(channels, "channel", "channels")}; the remaining channels are assigned to no speaker.", isMore ? "more" : "fewer");
        }
    }
}

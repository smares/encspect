using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

internal static class WavHeaderNodes
{
    // The nodes of the fields that were read, children of the header data's node, and the findings about what was read.
    public static void AddNodes(State s, WavFormat format, Node data, int channels)
    {
        FindingCollector findings = s.Findings;
        Node? tag = null;
        Node? count = null;
        Node? mask = null;
        if (format.Tag >= 0)
        {
            tag = data.Add("Format Tag", format.TagAt, 2, WavHeader.TagText(format.Tag), Explain.WavFormatTag, Topics.WaveFormat);
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
            case WavProblem.ChunkSizeInvalid:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", data, _ =>
                    $"The Wave64 chunk at 0x{format.ProblemAt:X} states {Counts.Of(format.ProblemSize, "byte", "bytes")}, but a size counts the 24 bytes of the chunk's own header, so none is under 24; the channels and the channel mask are not read.", "chunk-size");
                break;
            case WavProblem.TooManyChunks:
                findings.Add(Severity.Warning, "monkeys-audio.wav-header-damaged", data, _ =>
                    $"The WAV header data holds more than {WavHeader.MaxChunksBeforeFormat} chunks before a fmt chunk, which the analyzer does not follow, so the channels and the channel mask are not read.", "chunks");
                break;
        }

        if (tag is not null && format.Tag == WavHeader.WaveExtensible && format.ExtraSize >= 0 && format.Mask is null)
        {
            int stated = format.ExtraSize;
            long held = format.ExtraHeld;
            findings.Add(Severity.Warning, "monkeys-audio.wav-extensible-incomplete", tag, _ => stated > held
                ? $"The format tag is 0xFFFE (WAVEFORMATEXTENSIBLE), whose cbSize is at least 22 for the valid bits, the channel mask and the sub-format, but it states {Counts.Of(stated, "byte", "bytes")} and the fmt chunk holds only {held} after it, so the channel mask is not read."
                : $"The format tag is 0xFFFE (WAVEFORMATEXTENSIBLE), whose cbSize is at least 22 for the valid bits, the channel mask and the sub-format, but it states {Counts.Of(stated, "byte", "bytes")}, so the channel mask is not read.", stated > held ? "cut" : "short");
        }

        if (tag is not null && format.IsCbSizeMissing)
        {
            findings.Add(Severity.Warning, "monkeys-audio.wav-extensible-incomplete", tag, _ =>
                "The format tag is 0xFFFE (WAVEFORMATEXTENSIBLE), whose cbSize is at least 22 for the valid bits, the channel mask and the sub-format, but the fmt chunk holds no cbSize, so the channel mask is not read.", "no-cbsize");
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

using Encspect.Core;

namespace Encspect.Formats.Opus;

// What a packet's TOC byte says (RFC 6716, section 3.1): the configuration number in the top five
// bits gives the mode, the bandwidth and the frame size (table 2), then the stereo flag, then the
// frame count code in the bottom two bits.
internal static class Toc
{
    public static int Config(byte toc) => toc >> 3;

    public static bool IsStereo(byte toc) => (toc & 0x04) != 0;

    public static int Code(byte toc) => toc & 0x03;

    // 0 SILK-only (configurations 0 to 11), 1 hybrid (12 to 15), 2 CELT-only (16 to 31).
    public static int Mode(byte toc) => Config(toc) switch
    {
        < 12 => 0,
        < 16 => 1,
        _ => 2,
    };

    // 0 narrowband, 1 mediumband, 2 wideband, 3 super-wideband, 4 fullband.
    public static int Bandwidth(byte toc) => Config(toc) switch
    {
        < 4 => 0,
        < 8 => 1,
        < 12 => 2,
        < 14 => 3,
        < 16 => 4,
        < 20 => 0,
        < 24 => 2,
        < 28 => 3,
        _ => 4,
    };

    // The frame size in samples at 48 kHz: SILK 10, 20, 40 and 60 ms, hybrid 10 and 20 ms, CELT
    // 2.5, 5, 10 and 20 ms.
    public static int FrameSamples(byte toc)
    {
        int config = Config(toc);
        return config switch
        {
            < 12 => (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 },
            < 16 => (config & 1) == 0 ? 480 : 960,
            _ => 120 << (config & 3),
        };
    }

    public static readonly string[] ModeNames = ["SILK-only", "Hybrid", "CELT-only"];

    public static readonly string[] BandwidthNames = ["Narrowband", "Mediumband", "Wideband", "Super-wideband", "Fullband"];

    // The audio bandwidth each name stands for (RFC 6716, table 1).
    public static readonly string[] BandwidthRanges = ["4 kHz", "6 kHz", "8 kHz", "12 kHz", "20 kHz"];

    public static string FrameDuration(int samples) => samples == 120 ? "2.5 ms" : $"{samples / 48} ms";

    public static string Describe(byte toc) =>
        $"{ModeNames[Mode(toc)]}, {BandwidthNames[Bandwidth(toc)].ToLowerInvariant()}, {FrameDuration(FrameSamples(toc))}";
}

// How a packet, or one stream's part of a multistream packet, breaks RFC 6716: the rules of section
// 3.4, and self-delimiting lengths (appendix B) that run past the packet.
internal enum PartStatus : byte
{
    Valid = 0,
    R1 = 1,
    R2 = 2,
    R3 = 3,
    R4 = 4,
    R5 = 5,
    R6 = 6,
    R7 = 7,
    SelfDelimitedOverrun = 8,

    // A stream's part that an earlier part's damage leaves unlocated.
    Missing = 9,
}

// One stream's part of a packet: its bytes, where its frame lengths start in the shared list, the
// bytes before its first frame (TOC, frame count byte, padding length and frame length bytes) and of
// those the padding length bytes and the padding they code (clamped), its TOC and frame count byte, its frame count and what is wrong
// with it. 24 bytes.
internal readonly record struct PartRecord(int Length, int FirstFrame, int Header, int PaddingLengthBytes, int Padding, byte TocByte, byte CountByte, byte Frames, PartStatus Status)
{
    public bool HasToc => Length > 0 && Status != PartStatus.Missing;

    public int Code => Toc.Code(TocByte);

    public bool HasCountByte => HasToc && Code == 3 && Header >= 2;

    public bool IsVbr => (CountByte & 0x80) != 0;

    public bool HasPadding => (CountByte & 0x40) != 0;

    // The frames opus_packet_get_nb_frames counts from the TOC and frame count byte alone, which
    // decoders take a packet's duration from (0 where the frame count byte is missing).
    public int DeclaredFrames => !HasToc ? 0 : Code switch
    {
        0 => 1,
        1 or 2 => 2,
        _ => Header >= 2 || Length >= 2 ? CountByte & 0x3F : 0,
    };

    // The samples at 48 kHz the part codes, by its TOC; 0 where its frames would last more than
    // 120 ms, which libopus refuses.
    public int Samples
    {
        get
        {
            int samples = DeclaredFrames * Toc.FrameSamples(TocByte);
            return samples > 5760 ? 0 : samples;
        }
    }
}

// Reads one part's framing (RFC 6716, sections 3.2 and 3.4, and appendix B for the self-delimiting
// framing of every stream of a multistream packet but the last).
internal static class Framing
{
    public const int MaxFrameLength = 1275;

    private static string Bytes(long count) => count == 1 ? "1 byte" : $"{count} bytes";

    // Reads the part at the start of data and adds its frame lengths to frames. The part takes
    // its record's Length bytes: all of data unless it is self-delimited.
    public static PartRecord Read(ReadOnlySpan<byte> data, bool selfDelimited, ChunkedList<ushort> frames, out string? error)
    {
        error = null;
        int first = frames.Count;
        if (data.Length == 0)
        {
            error = "the packet is empty, where every packet holds at least its TOC byte";
            return new PartRecord(0, first, 0, 0, 0, 0, 0, 0, PartStatus.R1);
        }

        byte toc = data[0];
        int position = 1;
        int length;
        switch (Toc.Code(toc))
        {
            case 0:
                {
                    if (selfDelimited)
                    {
                        if (!ReadLength(data, ref position, out length))
                        {
                            return Fail(data.Length, first, position, toc, 0, PartStatus.SelfDelimitedOverrun, "the self-delimiting frame length is missing", out error);
                        }
                    }
                    else
                    {
                        length = data.Length - position;
                    }

                    if (length > MaxFrameLength)
                    {
                        return Fail(data.Length, first, position, toc, 0, PartStatus.R2, $"its frame of {length} bytes is longer than the 1275 bytes a frame may take", out error);
                    }

                    return Finish(data.Length, frames, first, position, 0, toc, 0, [length], 0, selfDelimited, out error);
                }

            case 1:
                {
                    if (selfDelimited)
                    {
                        if (!ReadLength(data, ref position, out length))
                        {
                            return Fail(data.Length, first, position, toc, 0, PartStatus.SelfDelimitedOverrun, "the self-delimiting frame length is missing", out error);
                        }
                    }
                    else
                    {
                        int rest = data.Length - position;
                        if (rest % 2 != 0)
                        {
                            return Fail(data.Length, first, position, toc, 0, PartStatus.R3, $"its {Bytes(rest)} after the TOC byte do not split into two frames of equal size", out error);
                        }

                        length = rest / 2;
                    }

                    if (length > MaxFrameLength)
                    {
                        return Fail(data.Length, first, position, toc, 0, PartStatus.R2, $"its two frames of {length} bytes are longer than the 1275 bytes a frame may take", out error);
                    }

                    return Finish(data.Length, frames, first, position, 0, toc, 0, [length, length], 0, selfDelimited, out error);
                }

            case 2:
                {
                    if (!ReadLength(data, ref position, out int firstLength) || firstLength > data.Length - position)
                    {
                        return Fail(data.Length, first, Math.Min(position, data.Length), toc, 0, PartStatus.R4,
                            "the first frame's length is missing or larger than the bytes that follow it", out error);
                    }

                    int second;
                    if (selfDelimited)
                    {
                        if (!ReadLength(data, ref position, out second))
                        {
                            return Fail(data.Length, first, Math.Min(position, data.Length), toc, 0, PartStatus.SelfDelimitedOverrun, "the self-delimiting frame length is missing", out error);
                        }
                    }
                    else
                    {
                        second = data.Length - position - firstLength;
                    }

                    if (second > MaxFrameLength)
                    {
                        return Fail(data.Length, first, position, toc, 0, PartStatus.R2, $"its second frame of {second} bytes is longer than the 1275 bytes a frame may take", out error);
                    }

                    return Finish(data.Length, frames, first, position, 0, toc, 0, [firstLength, second], 0, selfDelimited, out error);
                }

            default:
                return ReadCode3(data, selfDelimited, frames, first, toc, out error);
        }
    }

    private static PartRecord ReadCode3(ReadOnlySpan<byte> data, bool selfDelimited, ChunkedList<ushort> frames, int first, byte toc, out string? error)
    {
        if (data.Length < 2)
        {
            return Fail(data.Length, first, 1, toc, 0, PartStatus.R6, "it ends after its TOC byte, without the frame count byte a code 3 packet needs", out error);
        }

        byte count = data[1];
        bool vbr = (count & 0x80) != 0;
        int frameCount = count & 0x3F;
        int position = 2;
        if (frameCount == 0)
        {
            return Fail(data.Length, first, position, toc, count, PartStatus.R5, "it declares no frames", out error);
        }

        if (frameCount * Toc.FrameSamples(toc) > 5760)
        {
            return Fail(data.Length, first, position, toc, count, PartStatus.R5,
                $"its {frameCount} frames of {Toc.FrameDuration(Toc.FrameSamples(toc))} last more than the 120 ms a packet may hold", out error);
        }

        PartStatus sizeRule = vbr ? PartStatus.R7 : PartStatus.R6;
        long padding = 0;
        int paddingLengthBytes = 0;
        if ((count & 0x40) != 0)
        {
            int value;
            do
            {
                if (position >= data.Length)
                {
                    return Fail(data.Length, first, position, toc, count, sizeRule, "its padding length runs past the end of the packet", out error, paddingLengthBytes, padding);
                }

                value = data[position++];
                paddingLengthBytes++;
                padding += value == 255 ? 254 : value;
            }
            while (value == 255);
        }

        var lengths = new int[frameCount];
        if (vbr)
        {
            long sum = 0;
            for (int i = 0; i < frameCount - 1; i++)
            {
                if (!ReadLength(data, ref position, out lengths[i]))
                {
                    return Fail(data.Length, first, Math.Min(position, data.Length), toc, count, PartStatus.R7, "its frame lengths run past the end of the packet", out error, paddingLengthBytes, padding);
                }

                sum += lengths[i];
            }

            if (selfDelimited)
            {
                if (!ReadLength(data, ref position, out lengths[^1]))
                {
                    return Fail(data.Length, first, Math.Min(position, data.Length), toc, count, PartStatus.SelfDelimitedOverrun, "the self-delimiting frame length is missing", out error, paddingLengthBytes, padding);
                }
            }
            else
            {
                long last = data.Length - position - padding - sum;
                if (last < 0)
                {
                    return Fail(data.Length, first, position, toc, count, PartStatus.R7,
                        "its header, frame lengths and padding take more bytes than the packet holds", out error, paddingLengthBytes, padding);
                }

                if (last > MaxFrameLength)
                {
                    return Fail(data.Length, first, position, toc, count, PartStatus.R2, $"its last frame of {last} bytes is longer than the 1275 bytes a frame may take", out error, paddingLengthBytes, padding);
                }

                lengths[^1] = (int)last;
            }
        }
        else
        {
            int length;
            if (selfDelimited)
            {
                if (!ReadLength(data, ref position, out length))
                {
                    return Fail(data.Length, first, Math.Min(position, data.Length), toc, count, PartStatus.SelfDelimitedOverrun, "the self-delimiting frame length is missing", out error, paddingLengthBytes, padding);
                }
            }
            else
            {
                long rest = data.Length - position - padding;
                if (rest < 0 || rest % frameCount != 0 || rest / frameCount > MaxFrameLength)
                {
                    // The equal frames of a CBR packet are implicit, so one over 1275 bytes breaks R2.
                    bool tooLong = rest >= 0 && rest % frameCount == 0;
                    return Fail(data.Length, first, position, toc, count, tooLong ? PartStatus.R2 : PartStatus.R6,
                        rest < 0 ? "its padding takes more bytes than the packet holds"
                        : !tooLong ? $"its {Bytes(rest)} of frame data do not split into {frameCount} frames of equal size"
                        : $"its frames of {rest / frameCount} bytes are longer than the 1275 bytes a frame may take", out error, paddingLengthBytes, padding);
                }

                length = (int)(rest / frameCount);
            }

            Array.Fill(lengths, length);
        }

        return Finish(data.Length, frames, first, position, paddingLengthBytes, toc, count, lengths, padding, selfDelimited, out error);
    }

    // A frame length (section 3.2.1): one byte below 252, or two, the second counting fours.
    private static bool ReadLength(ReadOnlySpan<byte> data, ref int position, out int length)
    {
        length = 0;
        if (position >= data.Length)
        {
            return false;
        }

        int b = data[position];
        if (b < 252)
        {
            position++;
            length = b;
            return true;
        }

        if (position + 1 >= data.Length)
        {
            position = data.Length;
            return false;
        }

        length = (data[position + 1] * 4) + b;
        position += 2;
        return true;
    }

    private static PartRecord Finish(int available, ChunkedList<ushort> frames, int first, int header, int paddingLengthBytes, byte toc, byte count, int[] lengths, long padding,
        bool selfDelimited, out string? error)
    {
        error = null;
        long total = header + padding;
        foreach (int length in lengths)
        {
            total += length;
        }

        if (total > available)
        {
            return Fail(available, first, header, toc, count, selfDelimited ? PartStatus.SelfDelimitedOverrun : PartStatus.R7,
                selfDelimited ? "its self-delimiting frame lengths run past the end of the packet" : "its frames run past the end of the packet", out error, paddingLengthBytes, padding);
        }

        foreach (int length in lengths)
        {
            frames.Add((ushort)length);
        }

        int length0 = selfDelimited ? (int)total : available;
        return new PartRecord(length0, first, header, paddingLengthBytes, (int)Math.Min(padding, int.MaxValue), toc, count, (byte)lengths.Length, PartStatus.Valid);
    }

    // A part whose framing breaks a rule takes the rest of the packet, with no frames.
    private static PartRecord Fail(int available, int first, int header, byte toc, byte count, PartStatus status, string message, out string error, int paddingLengthBytes = 0, long padding = 0)
    {
        error = message;
        return new PartRecord(available, first, Math.Min(header, available), paddingLengthBytes, (int)Math.Min(padding, int.MaxValue), toc, count, 0, status);
    }
}

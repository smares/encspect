using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// What a Windows Media Audio Voice stream is configured with and what follows from it, worked out as FFmpeg's
// decoder does (wmavoice.c wmavoice_decode_init, FFmpeg 6.1.1): the 46 bytes of codec-specific data, the width of the
// spillover field of a packet header, the frame type table and the pitch ranges that decide how wide a frame's pitch
// fields are. Microsoft has published no specification of the codec.
internal sealed class VoiceParameters
{
    public const int ExtraDataSize = 46;

    // Bytes 0 to 17 of the codec data are laid out as Professional's (wmavoice.c); bytes 18 to 21 hold the flags and bytes 22 to 45 the table.
    public const int FlagsOffset = 18;
    public const int TableOffset = 22;

    public const int FrameTypes = 17;
    public const int FrameSamples = 160;
    public const int FramesPerSuperframe = 3;
    public const int SuperframeSamples = FrameSamples * FramesPerSuperframe;

    // wmavoice.c: MAX_SIGNAL_HISTORY, which the pitch range (max_pitch_val + 8) must fit in.
    private const int MaxSignalHistory = 416;

    // wmavoice.c: SFRAME_CACHE_MAXSIZE, the bytes a decoder keeps of a superframe that a packet ends in.
    public const int CacheBytes = 256;

    public int Channels { get; private init; }

    public int SampleRate { get; private init; }

    public int BlockAlign { get; private init; }

    public uint Flags { get; private init; }

    public bool HasPostfilter => (Flags & 1) != 0;

    public int DenoiseStrength => (int)((Flags >> 2) & 0xF);

    public bool HasDenoiseTiltCorrection => (Flags & 0x40) != 0;

    public int DcLevel => (int)((Flags >> 7) & 0xF);

    public int LspCount => (Flags & 0x1000) != 0 ? 16 : 10;

    public int LspQuantizerMode => (Flags & 0x2000) != 0 ? 1 : 0;

    public int LspDefaultMode => (Flags & 0x4000) != 0 ? 1 : 0;

    // The width of the spillover field in a packet's header: 3 + ceil(log2(block alignment)).
    public int SpilloverBits { get; private init; }

    // The frame type of each of the table's 25 slots, or -1; a code of the frame type VLC is a slot.
    public sbyte[] FrameTypeTable { get; private init; } = [];

    // The VLC symbol (slot) each frame type is reached by, or -1 for a type that no code reaches.
    public int[] SlotOfType { get; private init; } = [];

    public int MinPitch { get; private init; }

    public int MaxPitch { get; private init; }

    public int PitchBits { get; private init; }

    public int BlockPitchBits { get; private init; }

    public int BlockPitchRange { get; private init; }

    public int BlockDeltaPitchBits { get; private init; }

    public int BlockDeltaPitchHalfRange { get; private init; }

    public int[] BlockConversion { get; private init; } = [];

    // Bits of the LSPs of one frame (independent coding) and of a superframe that has them for all its frames (residual coding).
    public int IndependentLspBits => LspCount == 10 ? 24 : 34;

    public int ResidualLspBits => IndependentLspBits + (LspCount == 10 ? 24 : 26);

    // What FFmpeg's decoder rejects, in the order it checks: the code of the finding and its message. Empty when the
    // stream is read.
    public IReadOnlyList<(string Code, string Message)> Issues { get; private init; } = [];

    public bool IsUsable => Issues.Count == 0;

    // The first group of the frame type table (other than the last) that holds a fourth type, which takes the first slot of the next group
    // (the check of FFmpeg's decoder allows it, its comment says a group should hold three), or -1.
    public int OverfullGroup { get; private init; } = -1;

    public static VoiceParameters Create(AudioFormat format, ReadOnlySpan<byte> data)
    {
        var issues = new List<(string, string)>();
        uint flags = data.Length >= FlagsOffset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data[FlagsOffset..]) : 0;
        var empty = new VoiceParameters
        {
            Channels = format.Channels,
            SampleRate = format.SampleRate,
            BlockAlign = format.BlockAlign,
            Flags = flags,
        };

        if (data.Length != ExtraDataSize)
        {
            return empty.With(issues, "wma.codec-data-size", $"The codec-specific data of Windows Media Audio Voice is {Counts.Of(data.Length, "byte", "bytes")}; FFmpeg's decoder needs exactly 46 and refuses any other size (wmavoice.c)");
        }

        if (format.BlockAlign <= 0 || format.BlockAlign > 1 << 22)
        {
            return empty.With(issues, "wma.voice-block-alignment", $"The block alignment is {format.BlockAlign}; FFmpeg's decoder accepts 1 to 4,194,304 (wmavoice.c)");
        }

        int denoise = (int)((flags >> 2) & 0xF);
        if (denoise >= 12)
        {
            return empty.With(issues, "wma.voice-denoise-strength", $"The flags set a denoise strength of {denoise}; FFmpeg's decoder accepts 0 to 11 (wmavoice.c)");
        }

        // wmavoice.c: decode_vbmtree: 17 groups of 3 bits, each the slot group of the frame type of its index.
        var table = new sbyte[25];
        Array.Fill(table, (sbyte)-1);
        var counts = new int[8];
        int overfull = -1;
        for (int type = 0; type < FrameTypes; type++)
        {
            int group = ReadBits(data[TableOffset..], type * 3, 3);
            if (counts[group] > 3)
            {
                return empty.With(issues, "wma.voice-frame-type-table", $"The frame type table puts more than four frame types in one group of codes (type {type} in group {group}); FFmpeg's decoder rejects it (wmavoice.c: decode_vbmtree)");
            }

            if (counts[group] == 3 && group != 7 && overfull < 0)
            {
                overfull = group;
            }

            table[(group * 3) + counts[group]++] = (sbyte)type;
        }

        if (format.SampleRate >= int.MaxValue / (256 * 37))
        {
            return empty.With(issues, "wma.voice-sample-rate", $"The sample rate is {format.SampleRate} Hz; FFmpeg's decoder rejects rates of {int.MaxValue / (256 * 37)} Hz and more (wmavoice.c)");
        }

        long rate = format.SampleRate;
        int min = (int)((((rate << 8) / 400) + 50) >> 8);
        int max = (int)((((((rate << 8) * 37) / 2000) + 50)) >> 8);
        int range = max - min;
        if (range <= 0)
        {
            return empty.With(issues, "wma.voice-sample-rate", $"The sample rate of {format.SampleRate} Hz gives a pitch range of {Counts.Of(range, "sample", "samples")}; FFmpeg's decoder rejects it (wmavoice.c: Invalid pitch range)");
        }

        if (min < 1 || max + 8 > MaxSignalHistory)
        {
            return empty.With(issues, "wma.voice-sample-rate", $"The sample rate of {format.SampleRate} Hz is below 322 Hz or above 22,097 Hz, which FFmpeg's decoder rejects (wmavoice.c: Unsupported samplerate)");
        }

        int[] conversion = [min, (range * 25) >> 6, (range * 44) >> 6, max - 1];
        int halfRange = (range >> 3) & ~0xF;
        if (halfRange <= 0)
        {
            return empty.With(issues, "wma.voice-sample-rate", $"The sample rate of {format.SampleRate} Hz gives a delta pitch range of {halfRange}; FFmpeg's decoder rejects it (wmavoice.c: Invalid delta pitch hrange), and so accepts only 7,990 to 22,097 Hz");
        }

        int blockRange = conversion[2] + conversion[3] + 1 + (2 * (conversion[1] - (2 * min)));
        int[] slots = Slots(data[TableOffset..]);
        return new VoiceParameters
        {
            Channels = format.Channels,
            SampleRate = format.SampleRate,
            BlockAlign = format.BlockAlign,
            Flags = flags,
            SpilloverBits = 3 + CeilLog2(format.BlockAlign),
            FrameTypeTable = table,
            SlotOfType = slots,
            MinPitch = min,
            MaxPitch = max,
            PitchBits = CeilLog2(range),
            BlockConversion = conversion,
            BlockDeltaPitchHalfRange = halfRange,
            BlockDeltaPitchBits = 1 + CeilLog2(halfRange),
            BlockPitchRange = blockRange,
            BlockPitchBits = CeilLog2(blockRange),
            OverfullGroup = overfull,
        };
    }

    // The code each frame type is reached by, as a slot of the frame type VLC, or -1 for a type that no code reaches: one placed in a
    // slot past the 22 codes, or one that a later type of the table took the slot of. The same for a table that FFmpeg's decoder rejects.
    internal static int[] Slots(ReadOnlySpan<byte> table)
    {
        var tree = new sbyte[FrameTypes * 3];
        Array.Fill(tree, (sbyte)-1);
        var counts = new int[8];
        for (int type = 0; type < FrameTypes; type++)
        {
            int group = ReadBits(table, type * 3, 3);
            int slot = (group * 3) + counts[group]++;
            if (slot < tree.Length)
            {
                tree[slot] = (sbyte)type;
            }
        }

        var slots = new int[FrameTypes];
        Array.Fill(slots, -1);
        for (int slot = 0; slot < VoiceTables.FrameTypeCodeLengths.Length; slot++)
        {
            if (tree[slot] >= 0)
            {
                slots[tree[slot]] = slot;
            }
        }

        return slots;
    }

    // The bits of a failed check are the finding; the figures that follow from a good configuration are left out.
    private VoiceParameters With(List<(string, string)> issues, string code, string message)
    {
        issues.Add((code, message));
        return new VoiceParameters
        {
            Channels = Channels,
            SampleRate = SampleRate,
            BlockAlign = BlockAlign,
            Flags = Flags,
            Issues = issues,
        };
    }

    // libavutil: av_ceil_log2.
    internal static int CeilLog2(int value) => value <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(value - 1));

    // The count bits at the bit position, most significant first.
    internal static int ReadBits(ReadOnlySpan<byte> data, int position, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int at = position + i;
            value = (value << 1) | ((data[at >> 3] >> (7 - (at & 7))) & 1);
        }

        return value;
    }
}

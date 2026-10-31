using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// What a Windows Media Audio Voice stream states about its coding: the WAVEFORMATEX figures and the 46 bytes of codec-specific data,
// the checks FFmpeg's decoder makes before it opens, and the widths every field then has (docs/design/wma-voice-format.md, "Codec data").
internal sealed class VoiceParameters
{
    public const int ExtraDataSize = 46;
    public const int FlagsOffset = 18;
    public const int TableOffset = 22;
    public const int FrameTypes = 17;
    public const int FrameSamples = 160;
    public const int FramesPerSuperframe = 3;
    public const int SuperframeSamples = 480;
    public const int CacheBytes = 256;
    public const int CacheBits = CacheBytes * 8;
    public const int SlotCount = 22;

    // The most a block alignment can be: FFmpeg's decoder takes 1 to 2^22.
    private const int MaxBlockAlign = 1 << 22;

    private VoiceParameters()
    {
    }

    public int SampleRate { get; private init; }

    public int BlockAlign { get; private init; }

    public uint Flags { get; private init; }

    public bool HasPostfilter { get; private init; }

    public int DenoiseStrength { get; private init; }

    public bool HasDenoiseTiltCorrection { get; private init; }

    public int DcLevel { get; private init; }

    public int LspCount { get; private init; }

    public int LspQuantizerMode { get; private init; }

    public int LspDefaultMode { get; private init; }

    public int SpilloverBits { get; private init; }

    // For each frame type its slot, or -1 where no code reaches it; and the type each slot holds, or -1.
    public int[] SlotOfType { get; private init; } = [];

    public int[] TypeOfSlot { get; private init; } = [];

    public int MinPitch { get; private init; }

    // The pitch bound Pmax; the longest pitch is one less.
    public int MaxPitch { get; private init; }

    public int PitchBits { get; private init; }

    public int BlockPitchBits { get; private init; }

    public int BlockDeltaPitchBits { get; private init; }

    // The block pitch scale: its three anchors beyond the first, the half step and the range.
    public int Anchor1 { get; private init; }

    public int Anchor2 { get; private init; }

    public int HalfStep { get; private init; }

    public int BlockPitchRange { get; private init; }

    public int IndependentLspBits { get; private init; }

    public int ResidualLspBits { get; private init; }

    // The first group other than 7 that has a fourth type, else -1.
    public int OverfullGroup { get; private init; } = -1;

    public bool IsUsable => Issues.Count == 0;

    // The finding the first failed check gives: its code and its message without the period.
    public IReadOnlyList<(string Code, string Message)> Issues { get; private init; } = [];

    public int Channels { get; private init; }

    public static VoiceParameters Create(AudioFormat format, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(format);

        int rate = format.SampleRate;
        int align = format.BlockAlign;
        VoiceParameters Failed(string code, string message, uint flags = 0) =>
            new() { SampleRate = rate, BlockAlign = align, Channels = format.Channels, Flags = flags, Issues = [(code, message)] };

        if (data.Length != ExtraDataSize)
        {
            return Failed("wma.codec-data-size",
                $"The codec-specific data of Windows Media Audio Voice is {Counts.Of(data.Length, "byte", "bytes")}; FFmpeg's decoder needs exactly 46 and refuses any other size (wmavoice.c)");
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[FlagsOffset..]);
        if (align is < 1 or > MaxBlockAlign)
        {
            return Failed("wma.voice-block-alignment", $"The block alignment is {align}; FFmpeg's decoder accepts 1 to 4,194,304 (wmavoice.c)", flags);
        }

        int denoise = (int)((flags >> 2) & 15);
        if (denoise > 11)
        {
            return Failed("wma.voice-denoise-strength", $"The flags set a denoise strength of {denoise}; FFmpeg's decoder accepts 0 to 11 (wmavoice.c)", flags);
        }

        int[] slotOfType = SlotsOf(data[TableOffset..], out int[] typeOfSlot, out int overfull, out int fifthType, out int fifthGroup);
        if (fifthType >= 0)
        {
            return Failed("wma.voice-frame-type-table",
                $"The frame type table puts more than four frame types in one group of codes (type {fifthType} in group {fifthGroup}); FFmpeg's decoder rejects it (wmavoice.c)", flags);
        }

        if (rate >= 226_719)
        {
            return Failed("wma.voice-sample-rate", $"The sample rate is {rate} Hz; FFmpeg's decoder rejects rates of 226719 Hz and more (wmavoice.c)", flags);
        }

        long r = Math.Max(rate, 0);
        int minPitch = (int)(((256 * r / 400) + 50) / 256);
        int maxPitch = (int)(((256 * 37 * r / 2000) + 50) / 256);
        int range = maxPitch - minPitch;
        if (range <= 0)
        {
            return Failed("wma.voice-sample-rate", $"The sample rate of {rate} Hz gives a pitch range of {Counts.Of(range, "sample", "samples")}; FFmpeg's decoder rejects it (wmavoice.c: Invalid pitch range)", flags);
        }

        if (minPitch < 1 || maxPitch + 8 > 416)
        {
            return Failed("wma.voice-sample-rate", $"The sample rate of {rate} Hz is below 322 Hz or above 22,097 Hz, which FFmpeg's decoder rejects (wmavoice.c: Unsupported samplerate)", flags);
        }

        int half = 16 * ((range / 8) / 16);
        if (half <= 0)
        {
            return Failed("wma.voice-sample-rate",
                $"The sample rate of {rate} Hz gives a delta pitch range of {half}; FFmpeg's decoder rejects it (wmavoice.c: Invalid delta pitch hrange), and so accepts only 7,990 to 22,097 Hz", flags);
        }

        int anchor1 = 25 * range / 64;
        int anchor2 = 44 * range / 64;
        int blockRange = anchor2 + (maxPitch - 1) + 1 + (2 * (anchor1 - (2 * minPitch)));
        bool lsp16 = (flags & 0x1000) != 0;
        return new VoiceParameters
        {
            SampleRate = rate,
            BlockAlign = align,
            Channels = format.Channels,
            Flags = flags,
            HasPostfilter = (flags & 1) != 0,
            DenoiseStrength = denoise,
            HasDenoiseTiltCorrection = (flags & 0x40) != 0,
            DcLevel = (int)((flags >> 7) & 15),
            LspCount = lsp16 ? 16 : 10,
            LspQuantizerMode = (int)((flags >> 13) & 1),
            LspDefaultMode = (int)((flags >> 14) & 1),
            SpilloverBits = 3 + CeilLog2(align),
            SlotOfType = slotOfType,
            TypeOfSlot = typeOfSlot,
            MinPitch = minPitch,
            MaxPitch = maxPitch,
            PitchBits = CeilLog2(range),
            Anchor1 = anchor1,
            Anchor2 = anchor2,
            HalfStep = half,
            BlockDeltaPitchBits = 1 + CeilLog2(half),
            BlockPitchRange = blockRange,
            BlockPitchBits = CeilLog2(blockRange),
            IndependentLspBits = lsp16 ? 34 : 24,
            ResidualLspBits = lsp16 ? 60 : 48,
            OverfullGroup = overfull,
        };
    }

    // For each frame type its slot (a code number from 0 to 21), or -1 where no code reaches it. It works on any table, also a rejected one.
    public static int[] Slots(ReadOnlySpan<byte> table) => SlotsOf(table, out _, out _, out _, out _);

    // The smallest k with 2^k >= value, and 0 for a value of 1 or less.
    public static int CeilLog2(long value) => value <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(value - 1));

    // The table is 17 groups of 3 bits from the first bit. Type t takes slot 3 * its group + the number of earlier types of the group; a later
    // type replaces an earlier one in a slot, and a type left in no slot from 0 to 21 cannot be sent.
    private static int[] SlotsOf(ReadOnlySpan<byte> table, out int[] typeOfSlot, out int overfull, out int fifthType, out int fifthGroup)
    {
        int[] taken = new int[8];
        int[] ownSlot = new int[FrameTypes];
        var holder = new Dictionary<int, int>();
        fifthType = -1;
        fifthGroup = -1;
        overfull = -1;
        for (int t = 0; t < FrameTypes; t++)
        {
            int group = 0;
            for (int bit = 0; bit < 3; bit++)
            {
                int position = (3 * t) + bit;
                int index = position >> 3;
                group = (group << 1) | (index < table.Length ? (table[index] >> (7 - (position & 7))) & 1 : 0);
            }

            int slot = (3 * group) + taken[group];
            if (taken[group] == 4 && fifthType < 0)
            {
                fifthType = t;
                fifthGroup = group;
            }

            if (taken[group] == 3 && group != 7 && overfull < 0)
            {
                overfull = group;
            }

            taken[group]++;
            ownSlot[t] = slot;
            holder[slot] = t;
        }

        typeOfSlot = new int[SlotCount];
        Array.Fill(typeOfSlot, -1);
        int[] slots = new int[FrameTypes];
        for (int t = 0; t < FrameTypes; t++)
        {
            slots[t] = ownSlot[t] < SlotCount && holder[ownSlot[t]] == t ? ownSlot[t] : -1;
            if (slots[t] >= 0)
            {
                typeOfSlot[slots[t]] = t;
            }
        }

        return slots;
    }

    // The group of codes of a frame type in the table, 0 to 7.
    public static int GroupOf(ReadOnlySpan<byte> table, int type)
    {
        int group = 0;
        for (int bit = 0; bit < 3; bit++)
        {
            int position = (3 * type) + bit;
            int index = position >> 3;
            group = (group << 1) | (index < table.Length ? (table[index] >> (7 - (position & 7))) & 1 : 0);
        }

        return group;
    }
}

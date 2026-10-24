using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// A restart header (docs/design/truehd-format.md, "Restart header"): 113 + 6 * (M + 1) bits and a CRC-8, at any bit of a substream.
internal readonly struct RestartHeader
{
    public const int Sync = 0x18F5;
    public const int FixedBits = 113;

    public bool IsSyncValid { get; init; }

    public int NoiseType { get; init; }

    public int OutputTiming { get; init; }

    public int FirstChannel { get; init; }

    public int LastChannel { get; init; }

    public int LastMatrixChannel { get; init; }

    public int NoiseShift { get; init; }

    public int NoiseSeed { get; init; }

    // The four fields FFmpeg's encoder fills with the largest output shift, LSB count and output bit depths of the stretch the header opens.
    public int Skipped4 { get; init; }

    public int Skipped5A { get; init; }

    public int Skipped5B { get; init; }

    public int Skipped5C { get; init; }

    public bool HasBlockSizeCheck { get; init; }

    public int LosslessCheck { get; init; }

    public int Skipped16 { get; init; }

    public byte StoredCrc { get; init; }

    public byte ComputedCrc { get; init; }

    // The bit of the substream the header starts at, and how many bits it covers without its CRC.
    public int StartBit { get; init; }

    public int Bits { get; init; }

    // The matrix channels' positions in the presentation's channel list, 6 bits each.
    public ulong Assignments { get; init; }

    public bool IsCrcValid => StoredCrc == ComputedCrc;

    public int Assignment(int matrixChannel) => (int)((Assignments >> (6 * matrixChannel)) & 0x3F);

    // The bits the header and its CRC take.
    public int TotalBits => Bits + 8;

    // Reads the header that starts at startBit of data; IsSyncValid is false, and nothing else is read, where the sync is wrong. Bits past
    // the data read as zero, so the caller checks that the data holds TotalBits.
    public static RestartHeader Read(ReadOnlySpan<byte> data, int startBit)
    {
        var bits = new BitCursor(data);
        bits.Skip(startBit);
        if (bits.Read(13) != Sync)
        {
            return new RestartHeader { StartBit = startBit };
        }

        int noiseType = (int)bits.Read(1);
        int timing = (int)bits.Read(16);
        int first = (int)bits.Read(4);
        int last = (int)bits.Read(4);
        int matrixChannel = (int)bits.Read(4);
        int noiseShift = (int)bits.Read(4);
        int seed = (int)bits.Read(23);
        int skipped4 = (int)bits.Read(4);
        int skipped5A = (int)bits.Read(5);
        int skipped5B = (int)bits.Read(5);
        int skipped5C = (int)bits.Read(5);
        bool check = bits.ReadBit();
        int lossless = (int)bits.Read(8);
        int skipped16 = (int)bits.Read(16);
        ulong assignments = 0;
        for (int m = 0; m <= matrixChannel; m++)
        {
            assignments |= (ulong)bits.Read(6) << (6 * m);
        }

        int count = FixedBits + (6 * (matrixChannel + 1));
        byte stored = (byte)bits.Read(8);
        byte computed = bits.IsOverrun ? (byte)0 : Checks.RestartHeader(data, startBit, count);
        return new RestartHeader
        {
            IsSyncValid = true,
            NoiseType = noiseType,
            OutputTiming = timing,
            FirstChannel = first,
            LastChannel = last,
            LastMatrixChannel = matrixChannel,
            NoiseShift = noiseShift,
            NoiseSeed = seed,
            Skipped4 = skipped4,
            Skipped5A = skipped5A,
            Skipped5B = skipped5B,
            Skipped5C = skipped5C,
            HasBlockSizeCheck = check,
            LosslessCheck = lossless,
            Skipped16 = skipped16,
            Assignments = assignments,
            StoredCrc = stored,
            ComputedCrc = computed,
            StartBit = startBit,
            Bits = count,
        };
    }

    // The limits on the header's values; None where it is fine. channelListSize is the presentation's channel count, or 8 where it is not known.
    public SubstreamFault Validate(bool isMlp, int channelListSize)
    {
        if (isMlp && NoiseType == 1)
        {
            return SubstreamFault.MlpNoiseType;
        }

        if (LastChannel > 7 || LastChannel < FirstChannel - 1)
        {
            return SubstreamFault.ChannelRange;
        }

        if (LastMatrixChannel > (isMlp ? 5 : 7))
        {
            return SubstreamFault.MatrixChannelLimit;
        }

        if (LastMatrixChannel > 5 && NoiseType == 0)
        {
            return SubstreamFault.UnsupportedMatrixChannels;
        }

        for (int m = 0; m <= LastMatrixChannel; m++)
        {
            // For MLP a position past M is invalid whatever the arrangement lists.
            if (Assignment(m) >= channelListSize || (isMlp && Assignment(m) > LastMatrixChannel))
            {
                return SubstreamFault.ChannelAssignment;
            }
        }

        return SubstreamFault.None;
    }

    // Applies the header's values to the state and the resets that follow it.
    public void ApplyTo(SubstreamState state)
    {
        state.ResetForRestart();
        state.NoiseType = NoiseType;
        state.NoiseShift = NoiseShift;
        state.NoiseSeed = NoiseSeed;
        state.FirstChannel = FirstChannel;
        state.LastChannel = LastChannel;
        state.LastMatrixChannel = LastMatrixChannel;
        state.HasBlockSizeCheck = HasBlockSizeCheck;
        state.LosslessCheck = LosslessCheck;
        for (int m = 0; m < SubstreamState.Channels; m++)
        {
            state.ChannelAssignment[m] = m <= LastMatrixChannel ? Assignment(m) : 0;
        }
    }
}

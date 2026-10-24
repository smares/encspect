namespace Encspect.Formats.TrueHd;

// One primitive matrix: it replaces matrix channel OutChannel by a weighted sum of all channels.
internal sealed class MatrixState
{
    public int OutChannel { get; set; }

    public int FractionalBits { get; set; }

    public bool HasLsbBypass { get; set; }

    // Noise type 1 only.
    public int NoiseShift { get; set; }

    // Fractions with 14 fractional bits, for the input channels 0 to M and, with noise type 0, the two noise channels after them.
    public int[] Coefficients { get; } = new int[10];

    public MatrixState Clone()
    {
        var copy = new MatrixState { OutChannel = OutChannel, FractionalBits = FractionalBits, HasLsbBypass = HasLsbBypass, NoiseShift = NoiseShift };
        Coefficients.CopyTo(copy.Coefficients, 0);
        return copy;
    }
}

// The parameters of one coded channel: its filters and how its residuals are coded.
internal sealed class ChannelState
{
    public const int MaxFirOrder = 8;
    public const int MaxIirOrder = 4;

    public int FirOrder { get; set; }

    public int FirShift { get; set; }

    public int[] FirCoefficients { get; } = new int[MaxFirOrder];

    public int IirOrder { get; set; }

    public int IirShift { get; set; }

    public int[] IirCoefficients { get; } = new int[MaxIirOrder];

    // The IIR filter's state values, newest first, when its parameters carried them; the decoder loads them and clears the flag.
    public bool HasIirState { get; set; }

    public int[] IirState { get; } = new int[MaxIirOrder];

    public int HuffmanOffset { get; set; }

    public int Codebook { get; set; }

    public int LsbCount { get; set; } = 24;

    // How often each filter's parameters were read in the unit being read (at most twice).
    public int FirReads { get; set; }

    public int IirReads { get; set; }

    public int FilterShift => FirOrder > 0 ? FirShift : IirShift;

    public void Reset()
    {
        FirOrder = 0;
        FirShift = 0;
        IirOrder = 0;
        IirShift = 0;
        HasIirState = false;
        HuffmanOffset = 0;
        Codebook = 0;
        LsbCount = 24;
        FirReads = 0;
        IirReads = 0;
    }

    public ChannelState Clone()
    {
        var copy = new ChannelState();
        copy.CopyFrom(this);
        return copy;
    }

    public void CopyFrom(ChannelState other)
    {
        FirOrder = other.FirOrder;
        FirShift = other.FirShift;
        other.FirCoefficients.CopyTo(FirCoefficients, 0);
        IirOrder = other.IirOrder;
        IirShift = other.IirShift;
        other.IirCoefficients.CopyTo(IirCoefficients, 0);
        HasIirState = other.HasIirState;
        other.IirState.CopyTo(IirState, 0);
        HuffmanOffset = other.HuffmanOffset;
        Codebook = other.Codebook;
        LsbCount = other.LsbCount;
        FirReads = other.FirReads;
        IirReads = other.IirReads;
    }
}

// What a substream's syntax holds from block to block: the restart header's values and the decoding parameters in force. The reader of the
// syntax updates it, and the decoder keeps one of its own and updates it with the same reader.
internal sealed class SubstreamState
{
    public const int Channels = 8;
    public const int MaxMatrices = 8;

    // Whether a restart header has been read since the last major sync: until then the substream cannot be read.
    public bool HasRestart { get; set; }

    public int NoiseType { get; set; }

    public int NoiseShift { get; set; }

    public int NoiseSeed { get; set; }

    public int FirstChannel { get; set; }

    public int LastChannel { get; set; }

    // M, the presentation's channel count less 1.
    public int LastMatrixChannel { get; set; }

    public bool HasBlockSizeCheck { get; set; }

    public int LosslessCheck { get; set; }

    public int[] ChannelAssignment { get; } = new int[Channels];

    public int ParameterFlags { get; set; } = 0xFF;

    public int BlockSize { get; set; } = 8;

    public int MatrixCount { get; set; }

    public MatrixState[] Matrices { get; } = CreateMatrices();

    public int[] OutputShift { get; } = new int[Channels];

    public int[] Quantizer { get; } = new int[Channels];

    public ChannelState[] Coded { get; } = CreateChannels();

    // How often the matrices were read in the unit being read (at most twice).
    public int MatrixReads { get; set; }

    public int CodedChannels => LastChannel - FirstChannel + 1;

    private static MatrixState[] CreateMatrices()
    {
        var matrices = new MatrixState[MaxMatrices];
        for (int i = 0; i < matrices.Length; i++)
        {
            matrices[i] = new MatrixState();
        }

        return matrices;
    }

    private static ChannelState[] CreateChannels()
    {
        var channels = new ChannelState[Channels];
        for (int i = 0; i < channels.Length; i++)
        {
            channels[i] = new ChannelState();
        }

        return channels;
    }

    // Applies the resets a restart header makes (docs/design/truehd-format.md, "Values a restart header resets"); the filters' histories are not
    // part of the syntax's state.
    public void ResetForRestart()
    {
        HasRestart = true;
        ParameterFlags = 0xFF;
        MatrixCount = 0;
        BlockSize = 8;
        Array.Clear(OutputShift);
        Array.Clear(Quantizer);
        foreach (ChannelState channel in Coded)
        {
            channel.Reset();
        }
    }

    public SubstreamState Clone()
    {
        var copy = new SubstreamState
        {
            HasRestart = HasRestart,
            NoiseType = NoiseType,
            NoiseShift = NoiseShift,
            NoiseSeed = NoiseSeed,
            FirstChannel = FirstChannel,
            LastChannel = LastChannel,
            LastMatrixChannel = LastMatrixChannel,
            HasBlockSizeCheck = HasBlockSizeCheck,
            LosslessCheck = LosslessCheck,
            ParameterFlags = ParameterFlags,
            BlockSize = BlockSize,
            MatrixCount = MatrixCount,
            MatrixReads = MatrixReads,
        };
        ChannelAssignment.CopyTo(copy.ChannelAssignment, 0);
        OutputShift.CopyTo(copy.OutputShift, 0);
        Quantizer.CopyTo(copy.Quantizer, 0);
        for (int i = 0; i < Matrices.Length; i++)
        {
            copy.Matrices[i] = Matrices[i].Clone();
        }

        for (int i = 0; i < Coded.Length; i++)
        {
            copy.Coded[i] = Coded[i].Clone();
        }

        return copy;
    }

    // Forgets that a restart header was read, as a major sync does.
    public void Forget() => HasRestart = false;
}

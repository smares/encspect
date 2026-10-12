namespace Encspect.Formats.Ac3;

// Why the reading of a frame's audio blocks stopped before its auxiliary data.
internal enum StopReason : byte
{
    None,

    // The file ends inside the frame.
    DataEnds,

    // A field reaches past the end of the frame (A/52 7.10.2, check 21).
    Overrun,

    // bsid 9 or 10, which A/52 does not define.
    UndefinedBsid,

    // E-AC-3 at a reduced sample rate (fscod2), whose bit allocation A/52 does not specify.
    ReducedRate,

    // E-AC-3's adaptive hybrid transform codes mantissas the analyzer does not step over.
    Aht,

    // E-AC-3's enhanced coupling, whose parameters the analyzer does not step over.
    EnhancedCoupling,

    // A value that makes the rest of the frame unreadable: chbwcod above 60, a coupling or spectral
    // extension range that is empty, exponents outside 0 to 24, a reserved delta bit allocation or
    // SNR offset strategy.
    Invalid,
}

// What reading a frame found, for the frame's record, the findings and the statistics.
internal sealed class FrameResult
{
    public StopReason Stop { get; set; }

    // The bit where reading stopped, and the field it stopped at.
    public int StopBit { get; set; }

    public int BlocksRead { get; set; }

    public int Blocks { get; set; }

    // Statistics of the blocks read.
    public int BlockSwitchBlocks { get; set; }

    public int DynamicRangeBlocks { get; set; }

    public int CouplingBlocks { get; set; }

    public int RematrixingBlocks { get; set; }

    public int RematrixedBands { get; set; }

    public int RematrixingBands { get; set; }

    public int SpectralExtensionBlocks { get; set; }

    public int DeltaBitAllocationBlocks { get; set; }

    public int SkipBytes { get; set; }

    // Exponent sets by strategy (D15, D25, D45, reuse) over the channels and blocks read.
    public int[] ExponentStrategies { get; } = new int[4];

    public long MantissaBits { get; set; }

    public int AuxiliaryBits { get; set; } = -1;

    public bool HasAuxiliaryLength { get; set; }

    // Fields of the bit stream information the statistics count.
    public int Dialnorm { get; set; } = -1;

    public int Bsmod { get; set; } = -1;

    public bool HasCompression { get; set; }

    public int DolbySurroundMode { get; set; } = -1;

    public ushort ChannelMap { get; set; }

    public bool HasChannelMap { get; set; }

    public void Reset(int blocks)
    {
        Stop = StopReason.None;
        StopBit = 0;
        BlocksRead = 0;
        Blocks = blocks;
        BlockSwitchBlocks = 0;
        DynamicRangeBlocks = 0;
        CouplingBlocks = 0;
        RematrixingBlocks = 0;
        RematrixedBands = 0;
        RematrixingBands = 0;
        SpectralExtensionBlocks = 0;
        DeltaBitAllocationBlocks = 0;
        SkipBytes = 0;
        Array.Clear(ExponentStrategies);
        MantissaBits = 0;
        AuxiliaryBits = -1;
        HasAuxiliaryLength = false;
        Dialnorm = -1;
        Bsmod = -1;
        HasCompression = false;
        DolbySurroundMode = -1;
        ChannelMap = 0;
        HasChannelMap = false;
    }
}

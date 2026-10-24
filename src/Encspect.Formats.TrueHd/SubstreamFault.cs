namespace Encspect.Formats.TrueHd;

// Why a substream cannot be read any further (docs/design/truehd-format.md, "Validity rules and damage").
internal enum SubstreamFault
{
    None,

    // The data ends inside a field or a block.
    Overrun,

    // A block has no restart header and none was read since the last major sync (or since reading began).
    NoRestartHeader,

    // The restart header does not begin with its sync.
    RestartSync,

    // MLP with the noise type that uses the noise table.
    MlpNoiseType,

    // The coded channels are beyond 7, or the last is more than one below the first.
    ChannelRange,

    // The last matrix channel is above the format's limit.
    MatrixChannelLimit,

    // A matrix channel's position does not exist in the presentation's channel list.
    ChannelAssignment,

    // The block size is below 8 or above the unit's samples.
    BlockSize,

    // More primitive matrices than the format allows.
    MatrixCount,

    // A matrix replaces a channel above the last matrix channel.
    MatrixChannel,

    // A matrix has more than 14 fractional bits.
    FractionalBits,

    // The matrices or a filter were read three times in one unit.
    TooManyReads,

    FilterOrder,

    CoefficientBits,

    // A FIR filter carries state.
    FirState,

    // The FIR and IIR orders add up to more than 8.
    OrderSum,

    // Both filters are in use and their shifts differ.
    ShiftsDiffer,

    // The LSB count is above 24, or below the quantizer step size, in a channel with a codebook.
    LsbCount,

    // A bit string that is no codeword of the channel's codebook.
    Codeword,

    // The blocks hold more samples than the unit.
    BlockPastUnit,

    // No bit is left for the last-block flag.
    NoBitForLastFlag,

    // The end of stream marker's words are wrong.
    EndOfStreamMarker,

    // What remains after the blocks, the marker and the check data does not add up.
    LengthMismatch,

    // A substream with check data is shorter than the two bytes of the check data.
    CheckData,

    // Unsupported, not damaged: more than 5 as the last matrix channel with noise type 0.
    UnsupportedMatrixChannels,

    // Unsupported, not damaged: a negative output shift.
    UnsupportedNegativeShift,
}

internal static class SubstreamFaults
{
    // Faults of features the format description leaves open, which the analyzer does not read, as opposed to damage.
    public static bool IsUnsupported(SubstreamFault fault) => fault is SubstreamFault.UnsupportedMatrixChannels or SubstreamFault.UnsupportedNegativeShift;

    public static string Describe(SubstreamFault fault) => fault switch
    {
        SubstreamFault.Overrun => "the data ends inside a field or a block",
        SubstreamFault.NoRestartHeader => "a block has no restart header, and none was read since the last major sync",
        SubstreamFault.RestartSync => "the restart header does not begin with its sync 0x18F5",
        SubstreamFault.MlpNoiseType => "an MLP restart header states the noise type that uses the noise table",
        SubstreamFault.ChannelRange => "the restart header's coded channels are out of range",
        SubstreamFault.MatrixChannelLimit => "the restart header's last matrix channel is above what the format allows",
        SubstreamFault.ChannelAssignment => "a matrix channel's position does not exist in the presentation's channel list",
        SubstreamFault.BlockSize => "a block size is below 8 or above the samples of the unit",
        SubstreamFault.MatrixCount => "more primitive matrices than the format allows",
        SubstreamFault.MatrixChannel => "a matrix replaces a channel above the last matrix channel",
        SubstreamFault.FractionalBits => "a matrix states more than 14 fractional bits",
        SubstreamFault.TooManyReads => "the matrices or a filter are read a third time in one unit",
        SubstreamFault.FilterOrder => "a filter's order is above its limit",
        SubstreamFault.CoefficientBits => "a filter's coefficient bits are out of range",
        SubstreamFault.FirState => "a FIR filter states filter state",
        SubstreamFault.OrderSum => "the FIR and IIR orders add up to more than 8",
        SubstreamFault.ShiftsDiffer => "a channel's FIR and IIR filters state different shifts",
        SubstreamFault.LsbCount => "a channel's LSB count is above 24 or below its quantizer step size",
        SubstreamFault.Codeword => "a residual is not a codeword of its channel's codebook",
        SubstreamFault.BlockPastUnit => "the blocks hold more samples than the unit",
        SubstreamFault.NoBitForLastFlag => "no bit is left for a block's last-block flag",
        SubstreamFault.EndOfStreamMarker => "the end of stream marker is wrong",
        SubstreamFault.LengthMismatch => "the substream's length does not match its blocks, marker and check data",
        SubstreamFault.CheckData => "a substream with check data is shorter than the check data",
        SubstreamFault.UnsupportedMatrixChannels => "a last matrix channel above 5 with the noise channels, which the analyzer does not read",
        SubstreamFault.UnsupportedNegativeShift => "a negative output shift, which the analyzer does not read",
        _ => "the substream is damaged",
    };
}

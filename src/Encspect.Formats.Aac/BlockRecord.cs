namespace Encspect.Formats.Aac;

// The record the walk stores for each raw data block that is decoded (docs/design/lossy-decoders.md, "The parser and decoder split"): one slot of Stride ints for each
// individual_channel_stream of a single channel, channel pair or LFE element, in the order the elements follow one another. The quantized values stay in the order the
// bitstream codes them (ISO/IEC 14496-3 4.6.3.3: per window group, per scalefactor band, the windows of the group, the lines of the band); the decoder puts them in place.
internal static class BlockRecord
{
    public const int Stride = 3072;
    public const int MaxSlots = 64;

    // Header, per slot.
    public const int Kind = 0;
    public const int Tag = 1;
    public const int WindowSequence = 2;
    public const int WindowShape = 3;
    public const int MaxSfb = 4;
    public const int Groups = 5;

    // The lengths of the window groups, four bits each, group g at bit 4 * g.
    public const int GroupLengths = 6;
    public const int GlobalGain = 7;
    public const int Flags = 8;
    public const int MsMask = 9;
    public const int PulseCount = 10;
    public const int PulseStart = 11;

    // Four pairs of (offset, amplitude).
    public const int Pulses = 12;
    public const int TnsFilters = 20;

    // Slot 0 only: what the block holds apart from channels, and how many slots it fills.
    public const int BlockFlags = 21;
    public const int Slots = 22;

    // Slot 0 only: how many slots of SBR payloads (SbrRecord) follow the channel streams' slots.
    public const int SbrSlots = 23;

    // Per band of a group, group g and band sfb at g * 64 + sfb.
    public const int Codebooks = 32;
    public const int ScaleFactors = Codebooks + 512;
    public const int MidSide = ScaleFactors + 512;
    public const int Spectrum = MidSide + 512;
    public const int Tns = Spectrum + 1024;

    // Per TNS filter: window, length in bands, order, direction (1 downward), coefficient resolution (3 or 4 bits), compression, then up to 20 coefficients as coded.
    public const int TnsFilterInts = 26;
    public const int TnsMaxFilters = 8;
    public const int TnsMaxCoefficients = 20;

    // Per slot, after the TNS filters: the prediction data of ics_info. Prediction: bit 0 predictor_data_present, bit 1 predictor_reset (AAC Main; ISO/IEC 13818-7 13.3.1);
    // the reset group; prediction_used as a bit mask of the bands (up to 41, so two ints). Then ltp_data of the object type AAC LTP (ISO/IEC 14496-3 4.4.2): ltp_data_present,
    // the lag, the coefficient index and ltp_long_used as a bit mask of the bands (up to 40).
    public const int Prediction = Tns + (TnsMaxFilters * TnsFilterInts);
    public const int PredictionGroup = Prediction + 1;
    public const int PredictionUsedLow = Prediction + 2;
    public const int PredictionUsedHigh = Prediction + 3;
    public const int LtpPresent = Prediction + 4;
    public const int LtpLag = Prediction + 5;
    public const int LtpCoefficient = Prediction + 6;
    public const int LtpUsedLow = Prediction + 7;
    public const int LtpUsedHigh = Prediction + 8;
    public const int PredictionInts = 9;
    public const int PredictionPresent = 1;
    public const int PredictionReset = 2;

    public const int KindSingle = 1;
    public const int KindPairFirst = 2;
    public const int KindPairSecond = 3;
    public const int KindLfe = 4;

    // The values of an SBR payload (SbrRecord), after the channel streams.
    public const int KindSbr = 5;

    public const int FlagPulse = 1;
    public const int FlagTns = 2;
    public const int FlagCommonWindow = 4;

    public const int BlockHasSbr = 1;
    public const int BlockHasCoupling = 2;
    public const int BlockHasPs = 4;

    // An SBR payload after a single channel, channel pair or LFE element: the evidence that makes an unsignaled stream HE-AAC (BlockResult.HasSbrEvidence).
    public const int BlockHasSbrEvidence = 8;
}

// The slots of the block being read, in an array of the reader's own that the block log copies into the buffers the session lends.
internal sealed class RecordStore
{
    private int[] _data = [];

    public int Slots { get; private set; }

    private int[] _sbr = [];

    // How many SBR payloads the block holds; their slots follow the channel streams' in what Seal returns.
    public int SbrCount { get; private set; }

    // More channel streams than a slot count of MaxSlots: nothing is stored for the rest, and the block is handed over not walked.
    public bool IsOverflow { get; private set; }

    public int Flags { get; set; }

    // The block's slots, with the number of slots and the block's flags in the header of the first; a block without a channel stream is one empty slot.
    public ReadOnlySpan<int> Seal()
    {
        if (Slots == 0)
        {
            if (_data.Length < BlockRecord.Stride)
            {
                Array.Resize(ref _data, BlockRecord.Stride);
            }

            _data.AsSpan(0, BlockRecord.Codebooks).Clear();
        }

        Span<int> first = At(0);
        first[BlockRecord.Slots] = Slots;
        first[BlockRecord.BlockFlags] = Flags;
        first[BlockRecord.SbrSlots] = SbrCount;
        int channelSlots = Math.Max(1, Slots);
        if (SbrCount > 0)
        {
            if (_data.Length < (channelSlots + SbrCount) * BlockRecord.Stride)
            {
                Array.Resize(ref _data, (channelSlots + SbrCount) * BlockRecord.Stride);
            }

            _sbr.AsSpan(0, SbrCount * BlockRecord.Stride).CopyTo(_data.AsSpan(channelSlots * BlockRecord.Stride));
        }

        return _data.AsSpan(0, (channelSlots + SbrCount) * BlockRecord.Stride);
    }

    public void Reset()
    {
        Slots = 0;
        SbrCount = 0;
        IsOverflow = false;
        Flags = 0;
    }

    // The next slot, cleared where the walk does not write every int; -1 where the block holds too many.
    public int Begin(int kind, int tag)
    {
        if (Slots >= BlockRecord.MaxSlots)
        {
            IsOverflow = true;
            return -1;
        }

        int slot = Slots++;
        if (_data.Length < Slots * BlockRecord.Stride)
        {
            Array.Resize(ref _data, Math.Min(BlockRecord.MaxSlots, Math.Max(Slots, _data.Length / BlockRecord.Stride * 2)) * BlockRecord.Stride);
        }

        Span<int> span = At(slot);
        span[..BlockRecord.Codebooks].Clear();
        span.Slice(BlockRecord.Spectrum, 1024).Clear();
        span.Slice(BlockRecord.Prediction, BlockRecord.PredictionInts).Clear();
        span[BlockRecord.Kind] = kind;
        span[BlockRecord.Tag] = tag;
        return slot;
    }

    // The next SBR payload's slot, cleared where the walk does not write every int; empty where the block holds more than MaxSbr.
    public Span<int> BeginSbr(int tag)
    {
        if (SbrCount >= MaxSbr)
        {
            return default;
        }

        if (_sbr.Length < (SbrCount + 1) * BlockRecord.Stride)
        {
            Array.Resize(ref _sbr, Math.Min(MaxSbr, Math.Max(SbrCount + 1, _sbr.Length / BlockRecord.Stride * 2)) * BlockRecord.Stride);
        }

        Span<int> span = _sbr.AsSpan(SbrCount * BlockRecord.Stride, BlockRecord.Stride);
        span[..SbrRecord.FirstChannel].Clear();
        span[BlockRecord.Kind] = BlockRecord.KindSbr;
        span[BlockRecord.Tag] = tag;
        SbrCount++;
        return span;
    }

    private const int MaxSbr = 16;

    public Span<int> At(int slot) => _data.AsSpan(slot * BlockRecord.Stride, BlockRecord.Stride);
}

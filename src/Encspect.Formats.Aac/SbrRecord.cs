namespace Encspect.Formats.Aac;

// The values of one SBR payload (ISO/IEC 14496-3 4.4.2, sbr_data and, after it, the parametric stereo extension's ps_data), stored for the decoder in one slot of the block's record
// (BlockRecord, KindSbr) beside the channel streams' slots. The header's fields come with every payload, whether or not that frame repeats the header: the decoder compares them with
// its own. Envelope and noise values stay as coded (the deltas and the start values), and the decoder adds them up in the direction each states.
internal static class SbrRecord
{
    // Header of the slot: BlockRecord.Kind and Tag come first.
    public const int ChannelSlot = 2;
    public const int Pair = 3;
    public const int HasHeader = 4;

    // The header's fields, in the order of sbr_header.
    public const int AmpRes = 5;
    public const int StartFrequency = 6;
    public const int StopFrequency = 7;
    public const int XoverBand = 8;
    public const int FrequencyScale = 9;
    public const int AlterScale = 10;
    public const int NoiseBands = 11;
    public const int LimiterBands = 12;
    public const int LimiterGains = 13;
    public const int InterpolatedFrequency = 14;
    public const int SmoothingMode = 15;
    public const int Coupling = 16;

    // 1 where sbr_data was read to its end; 0 where the data is damaged or no header is known, and the decoder treats the frame as one without SBR data.
    public const int Valid = 17;

    // 1 where the extension carries ps_data, read to its end (a single channel element only).
    public const int HasPs = 18;

    public const int FirstChannel = 32;
    public const int ChannelStride = 440;

    // Per channel, relative to its start.
    public const int FrameClass = 0;
    public const int EnvelopeCount = 1;
    public const int NoiseCount = 2;
    public const int ChannelAmpRes = 3;

    // The envelope that carries the transient, or -1 (bs_pointer's meaning in each frame class).
    public const int Transient = 4;
    public const int Borders = 5;
    public const int NoiseBorders = 11;
    public const int FrequencyResolution = 14;
    public const int EnvelopeTime = 19;
    public const int NoiseTime = 24;
    public const int InverseFilterMode = 26;
    public const int HasHarmonics = 31;
    public const int Harmonics = 32;

    // Per envelope 64 values: the first (the start value) and the deltas of a frequency-coded one, or one delta per band of a time-coded one.
    public const int Envelope = 80;
    public const int EnvelopeStride = 64;

    // Per noise floor 8 values.
    public const int Noise = 400;
    public const int NoiseStride = 8;

    public const int MaxEnvelopes = 5;

    // ps_data, as the decoder's PsDecoder reads it.
    public const int Ps = 920;
    public const int PsHasHeader = 0;
    public const int PsEnableIid = 1;
    public const int PsIidMode = 2;
    public const int PsIidFine = 3;
    public const int PsEnableIcc = 4;
    public const int PsIccMode = 5;
    public const int PsFrameClass = 6;
    public const int PsEnvelopes = 7;
    public const int PsBorders = 8;
    public const int PsIidTime = 16;
    public const int PsIccTime = 22;
    public const int PsIid = 30;
    public const int PsIcc = 200;
    public const int PsStride = 34;
    public const int PsMaxEnvelopes = 5;
}

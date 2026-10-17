namespace Encspect.Formats.Aac;

// The coding statistics of a stream's raw data blocks, counted while they are read, so nothing is
// kept per access unit: elements, window sequences and shapes, stereo coding, the tools each
// channel stream uses, the codebooks of its bands, max_sfb, global gain per channel, fill and data
// stream elements, SBR and PS. Channel streams are the individual_channel_streams of single
// channel, channel pair and LFE elements, counted once per access unit and channel, as MPEG
// statistics count granules per channel.
internal sealed class CodingTally
{
    public long Blocks { get; set; }

    public long DamagedBlocks { get; set; }

    public long[] Elements { get; } = new long[8];

    public long ChannelStreams { get; set; }

    // By window_sequence: only long, long start, eight short, long stop.
    public long[] WindowSequences { get; } = new long[4];

    // By window_shape: sine, KBD.
    public long[] WindowShapes { get; } = new long[2];

    // Channel streams with TNS data, by window sequence.
    public long[] Tns { get; } = new long[4];

    public long TnsFilters { get; set; }

    public long Pulses { get; set; }

    public long GainControl { get; set; }

    // AAC Main prediction: channel streams with predictor data, and those that reset a group.
    public long Prediction { get; set; }

    public long PredictorResets { get; set; }

    // AAC LTP: channel streams with ltp_data_present.
    public long LongTermPrediction { get; set; }

    // Scalefactor bands coded (window groups times max_sfb), per codebook.
    public long[] Codebooks { get; } = new long[16];

    public long Bands { get; set; }

    public long NoiseStreams { get; set; }

    public long NoiseBands { get; set; }

    // max_sfb of long and of eight short windows.
    public long[] MaxSfbLong { get; } = new long[64];

    public long[] MaxSfbShort { get; } = new long[16];

    // Channel pair elements: with a common window, by ms_mask_present, and their bands.
    public long Pairs { get; set; }

    public long CommonWindow { get; set; }

    public long[] MidSideMasks { get; } = new long[4];

    // The bands of common window pairs, those coded as mid/side (not intensity stereo bands), and
    // the intensity stereo bands of the second channel, of which those out of phase.
    public long PairBands { get; set; }

    public long MidSideBands { get; set; }

    public long IntensityPairs { get; set; }

    public long IntensityBands { get; set; }

    public long IntensityOutOfPhase { get; set; }

    // global_gain per channel, in the order of the elements; -1 before the first.
    public List<int> MinimumGain { get; } = [];

    public List<int> MaximumGain { get; } = [];

    public long FillBytes { get; set; }

    // Extension payloads by extension_type, and their bytes.
    public long[] Extensions { get; } = new long[16];

    public long[] ExtensionBytes { get; } = new long[16];

    public long InvalidFillData { get; set; }

    public long DataStreamBytes { get; set; }

    public long AncillaryElements { get; set; }

    public long AncillaryBytes { get; set; }

    public long DynamicRangeBands { get; set; }

    // Access units with SBR data, with the parametric stereo extension, and SBR headers.
    public long SbrBlocks { get; set; }

    public long PsBlocks { get; set; }

    // What SBR and PS are inferred from where the configuration does not signal them (see
    // BlockLog.AddEvidence): the blocks read to their end that are not in a damaged frame, those among
    // them with an SBR payload that was read in full, and with the parametric stereo extension.
    public long EvidenceBlocks { get; set; }

    public long SbrEvidenceBlocks { get; set; }

    public long PsEvidenceBlocks { get; set; }

    public int FirstSbrEvidenceBlock { get; set; } = -1;

    // The rule for inferring SBR (and PS) from the blocks: in at least two of them, or in the only one
    // there is, and in at least half. A damaged frame's blocks, a payload before any channel element and
    // a lone occurrence in an otherwise plain stream therefore do not turn it into HE-AAC.
    public bool IsSbrEvident => IsEvident(SbrEvidenceBlocks);

    public bool IsPsEvident => IsEvident(PsEvidenceBlocks);

    private bool IsEvident(long count) => count > 0 && count >= Math.Min(2, EvidenceBlocks) && count * 2 >= EvidenceBlocks;

    public int FirstSbrBlock { get; set; } = -1;

    public int FirstPsBlock { get; set; } = -1;

    public long SbrHeaders { get; set; }

    public SbrHeader? FirstHeader { get; set; }

    public bool SbrHeaderChanged { get; set; }

    // SBR payloads read in full, and those of plain data; headers that FAAC 2.x's layout fits, and
    // whether every header after the first of its element came a multiple of FaacSbr.HeaderPeriod
    // access units after the one before (see FaacSbr).
    public long SbrPayloads { get; set; }

    public long SbrPlainPayloads { get; set; }

    public long SbrPlainHeaders { get; set; }

    public bool SbrHeadersOnPeriod { get; set; } = true;

    public long SbrUnread { get; set; }

    // Access units whose channel elements differ from what the channel configuration defines.
    public long Mismatched { get; set; }

    // Printable text in fill and data stream payloads: the first, its access unit and how often.
    public string? FirstText { get; set; }

    public int FirstTextBlock { get; set; } = -1;

    public long Texts { get; set; }

    // The channels whose global gain is counted, as the bound BlockLog keeps for a block; past them,
    // a crafted block of many single channel elements would add a statistics node per element.
    public const int MaxGainChannels = 48;

    public void Gain(int channel, int gain)
    {
        if (channel >= MaxGainChannels)
        {
            return;
        }

        while (MinimumGain.Count <= channel)
        {
            MinimumGain.Add(int.MaxValue);
            MaximumGain.Add(int.MinValue);
        }

        MinimumGain[channel] = Math.Min(MinimumGain[channel], gain);
        MaximumGain[channel] = Math.Max(MaximumGain[channel], gain);
    }
}

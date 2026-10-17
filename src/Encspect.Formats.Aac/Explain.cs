namespace Encspect.Formats.Aac;

// The background articles the AAC nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string ObjectTypes = "aac-object-types";
    public const string AudioSpecificConfig = "audio-specific-config";
    public const string RawDataBlocks = "aac-raw-data-blocks";
    public const string Sbr = "sbr";
    public const string ParametricStereo = "parametric-stereo";
    public const string Adts = "adts";
    public const string Usac = "usac";
    public const string Crc = "crc";
    public const string BitrateModes = "bitrate-modes";
    public const string EncoderDetection = "encoder-detection";
    public const string Windows = "aac-windows-and-blocks";
    public const string Codebooks = "aac-section-data-and-codebooks";
    public const string Tns = "tns";
    public const string Pns = "pns";
    public const string PulseData = "aac-pulse-data";
    public const string DynamicRange = "aac-dynamic-range-control";
    public const string MidSide = "mid-side-stereo";
    public const string Intensity = "intensity-stereo";
    public const string ChannelLayouts = "channel-layouts";
}

// What each AAC node is, in the words the report shows next to it. Constants only: access unit and
// frame nodes are built by the hundred thousand, so a description picks one of these and never
// allocates.
internal static partial class Explain
{
    public const string AudioSpecificConfig = "The AudioSpecificConfig: the object type, sample rate and channel configuration a decoder needs before the first access unit, which the container stores apart from the audio (ISO/IEC 14496-3 1.6.2.1).";
    public const string ObjectType = "The audio object type, the set of coding tools the stream uses: 2 is AAC LC, 5 SBR, 29 PS, 42 USAC. 31 escapes to six more bits for types from 32 on (ISO/IEC 14496-3 1.6.2.1, 1.5.1.1).";
    public const string CoreObjectType = "The object type of the core codec under SBR or PS, usually 2 for AAC LC (ISO/IEC 14496-3 1.6.2.1).";
    public const string SampleRate = "The sample rate of the core codec, from a 4-bit index or, after index 15, 24 bits stating it. With SBR the output rate can be twice this (ISO/IEC 14496-3 1.6.2.1, 1.6.3.4).";
    public const string ChannelConfiguration = "The channel layout by number: 1 mono, 2 stereo, up to 7 for 7.1, and 11 to 14 for further layouts of 6.1, 7.1 and 22.2; 0 leaves the layout to a program config element (ISO/IEC 14496-3 1.6.3.5).";
    public const string ExtensionSampleRate = "The output sample rate of SBR, usually twice the core's (ISO/IEC 14496-3 1.6.2.1).";
    public const string ExtensionChannelConfiguration = "The channel configuration of the BSAC extension layers (ISO/IEC 14496-3 1.6.2.1).";
    public const string SyncExtension = "The backward-compatible signaling of SBR and PS: a sync word after the core's configuration, which decoders that know nothing of SBR ignore (ISO/IEC 14496-3 1.6.2.1).";
    public const string SyncExtensionType = "The sync word of the extension: 0x2B7 before the SBR signaling, 0x548 before the PS signaling (ISO/IEC 14496-3 1.6.2.1).";
    public const string ExtensionObjectType = "The object type the extension signals: 5 for SBR, 22 for BSAC (ISO/IEC 14496-3 1.6.2.1).";
    public const string HasSbr = "Whether the stream uses spectral band replication: sbrPresentFlag. When it is set, the decoder outputs the extension sample rate, usually twice the core's (ISO/IEC 14496-3 1.6.2.1).";
    public const string HasPs = "Whether the stream uses parametric stereo: psPresentFlag. When it is set, a mono core decodes to stereo (ISO/IEC 14496-3 1.6.2.1).";
    public const string UnreadBits = "Bits after the configuration's last field that the analyzer does not read.";

    public const string FrameLength = "frameLengthFlag: how many samples per channel each access unit holds, 1024 or 960 (512 or 480 for the low delay types) (ISO/IEC 14496-3 4.4.1).";
    public const string DependsOnCoreCoder = "dependsOnCoreCoder: whether the stream is a layer of a scalable stream built on another core coder (ISO/IEC 14496-3 4.4.1).";
    public const string CoreCoderDelay = "The delay of the core coder, in samples, that a scalable layer accounts for (ISO/IEC 14496-3 4.4.1).";
    public const string HasExtension = "extensionFlag: whether the fields of the error resilient object types follow (ISO/IEC 14496-3 4.4.1).";
    public const string ProgramConfigElement = "A program config element: the channel layout spelled out element by element, where the channel configuration is 0 (ISO/IEC 14496-3 4.4.1.1).";
    public const string LayerNumber = "layerNr: which layer of a scalable stream this is (ISO/IEC 14496-3 4.4.1).";
    public const string SubframeCount = "numOfSubFrame: the BSAC subframes per frame (ISO/IEC 14496-3 4.4.1).";
    public const string LayerLength = "layer_length: the length of a BSAC layer (ISO/IEC 14496-3 4.4.1).";
    public const string SectionResilience = "aacSectionDataResilienceFlag: whether the section data is coded to survive bit errors (ISO/IEC 14496-3 4.4.1).";
    public const string ScalefactorResilience = "aacScalefactorDataResilienceFlag: whether the scalefactors are coded to survive bit errors (ISO/IEC 14496-3 4.4.1).";
    public const string SpectralResilience = "aacSpectralDataResilienceFlag: whether the spectral data is coded to survive bit errors (ISO/IEC 14496-3 4.4.1).";
    public const string HasExtension3 = "extensionFlag3, left for a later version of the specification (ISO/IEC 14496-3 4.4.1).";
    public const string ErrorProtectionConfig = "epConfig: how the error resilient stream is protected; 0 and 1 need no further configuration (ISO/IEC 14496-3 1.6.2.1).";

    public const string HasLowDelaySbr = "ldSbrPresentFlag: whether the low delay stream uses its own variant of SBR (ISO/IEC 14496-3, ELDSpecificConfig).";
    public const string LowDelaySbrRate = "ldSbrSamplingRate: whether low delay SBR runs at the core's rate (downsampled) or twice it (dual-rate) (ISO/IEC 14496-3, ELDSpecificConfig).";
    public const string HasLowDelaySbrCrc = "ldSbrCrcFlag: whether the low delay SBR data carries a CRC (ISO/IEC 14496-3, ELDSpecificConfig).";
    public const string SbrHeader = "An SBR header: the frequency range and resolution SBR uses, which the low delay configuration states once (ISO/IEC 14496-3, sbr_header).";
    public const string SbrAmpRes = "bs_amp_res: the resolution of the coded envelope, 1.5 or 3 dB steps (ISO/IEC 14496-3, sbr_header).";
    public const string SbrStartFreq = "bs_start_freq: where in the spectrum SBR starts, the top of what the core codes (ISO/IEC 14496-3, sbr_header).";
    public const string SbrStopFreq = "bs_stop_freq: where in the spectrum SBR ends (ISO/IEC 14496-3, sbr_header).";
    public const string SbrXoverBand = "bs_xover_band: an offset of the start of SBR within its frequency table (ISO/IEC 14496-3, sbr_header).";
    public const string SbrReserved = "Reserved bits of the SBR header (ISO/IEC 14496-3, sbr_header).";
    public const string SbrHeaderExtra1 = "bs_header_extra_1: whether the frequency scale fields follow; without them the defaults hold (ISO/IEC 14496-3, sbr_header).";
    public const string SbrHeaderExtra2 = "bs_header_extra_2: whether the limiter and smoothing fields follow; without them the defaults hold (ISO/IEC 14496-3, sbr_header).";
    public const string SbrFreqScale = "bs_freq_scale: how many SBR bands an octave holds (ISO/IEC 14496-3, sbr_header).";
    public const string SbrAlterScale = "bs_alter_scale: a coarser grouping of the SBR bands (ISO/IEC 14496-3, sbr_header).";
    public const string SbrNoiseBands = "bs_noise_bands: how many noise floor bands an octave holds (ISO/IEC 14496-3, sbr_header).";
    public const string SbrLimiterBands = "bs_limiter_bands: how many limiter bands an octave holds (ISO/IEC 14496-3, sbr_header).";
    public const string SbrLimiterGains = "bs_limiter_gains: how far the limiter lets the rebuilt bands' gain rise (ISO/IEC 14496-3, sbr_header).";
    public const string SbrInterpolFreq = "bs_interpol_freq: whether the envelope is interpolated across frequency (ISO/IEC 14496-3, sbr_header).";
    public const string SbrSmoothingMode = "bs_smoothing_mode: whether the gains are smoothed over time (ISO/IEC 14496-3, sbr_header).";
    public const string EldExtension = "An ELD extension of the configuration, with its type and length (ISO/IEC 14496-3, ELDSpecificConfig).";
    public const string EldExtensionType = "The type of the ELD extension; 0 ends the list (ISO/IEC 14496-3, ELDSpecificConfig).";
    public const string EldExtensionLength = "The length of the ELD extension in bytes; 15 adds 8 more bits, and their 255 adds 16 more (ISO/IEC 14496-3, ELDSpecificConfig).";
    public const string EldTerminator = "The ELD extension type 0, which ends the list of extensions (ISO/IEC 14496-3, ELDSpecificConfig).";

    public const string UsacConfig = "The USAC configuration: the output rate, the frame length and SBR ratio, the channel configuration and each element's settings (ISO/IEC 23003-3 5.2).";
    public const string UsacSampleRate = "usacSamplingFrequencyIndex: the output sample rate, after SBR, from a 5-bit index or 24 bits stating it (ISO/IEC 23003-3 6.1.1.2).";
    public const string CoreSbrFrameLengthIndex = "coreSbrFrameLengthIndex: the core's frame length and the SBR ratio, which together give the output samples per frame (ISO/IEC 23003-3).";
    public const string UsacChannelConfiguration = "channelConfigurationIndex: the channel layout by number, as for AAC; 0 spells the layout out (ISO/IEC 23003-3).";
    public const string UsacChannelConfig = "The channel layout spelled out: the number of output channels and each one's loudspeaker position (ISO/IEC 23003-3 5.2).";
    public const string OutputChannels = "numOutChannels: how many channels the decoder outputs (ISO/IEC 23003-3 5.2).";
    public const string OutputChannelPosition = "The loudspeaker position of one output channel (ISO/IEC 23003-3 5.2).";
    public const string UsacDecoderConfig = "The elements every USAC frame holds, in order, with each one's settings (ISO/IEC 23003-3 5.2).";
    public const string UsacElements = "numElements: how many elements every frame holds (ISO/IEC 23003-3 5.2).";
    public const string UsacElement = "One element of the USAC frames: a single channel, a channel pair, an LFE channel or an extension (ISO/IEC 23003-3 5.2).";
    public const string UsacElementType = "usacElementType: 0 a single channel element, 1 a channel pair element, 2 an LFE element, 3 an extension element (ISO/IEC 23003-3 5.2).";
    public const string TimeWarpedMdct = "tw_mdct: whether the element uses the time-warped transform (ISO/IEC 23003-3).";
    public const string NoiseFilling = "noiseFilling: whether the decoder fills spectral holes with noise (ISO/IEC 23003-3).";
    public const string HarmonicSbr = "harmonicSBR: whether SBR rebuilds the high band by harmonic transposition instead of copying (ISO/IEC 23003-3).";
    public const string InterTes = "bs_interTes: whether SBR shapes the rebuilt band's temporal envelope (ISO/IEC 23003-3).";
    public const string Pvc = "bs_pvc: whether SBR predicts the high band's envelope from the low band (ISO/IEC 23003-3).";
    public const string DefaultStartFreq = "dflt_start_freq: the default start of the SBR range, as bs_start_freq (ISO/IEC 23003-3).";
    public const string DefaultStopFreq = "dflt_stop_freq: the default end of the SBR range, as bs_stop_freq (ISO/IEC 23003-3).";
    public const string DefaultHeaderExtra1 = "dflt_header_extra1: whether default frequency scale fields follow (ISO/IEC 23003-3).";
    public const string DefaultHeaderExtra2 = "dflt_header_extra2: whether default limiter and smoothing fields follow (ISO/IEC 23003-3).";
    public const string DefaultFreqScale = "dflt_freq_scale: the default SBR bands per octave (ISO/IEC 23003-3).";
    public const string DefaultAlterScale = "dflt_alter_scale: the default coarser band grouping (ISO/IEC 23003-3).";
    public const string DefaultNoiseBands = "dflt_noise_bands: the default noise floor bands per octave (ISO/IEC 23003-3).";
    public const string DefaultLimiterBands = "dflt_limiter_bands: the default limiter bands per octave (ISO/IEC 23003-3).";
    public const string DefaultLimiterGains = "dflt_limiter_gains: the default limiter gain (ISO/IEC 23003-3).";
    public const string DefaultInterpolFreq = "dflt_interpol_freq: the default for interpolating the envelope across frequency (ISO/IEC 23003-3).";
    public const string DefaultSmoothingMode = "dflt_smoothing_mode: the default for smoothing the gains over time (ISO/IEC 23003-3).";
    public const string StereoConfigIndex = "stereoConfigIndex: 0 codes the pair as two channels; 1 to 3 use MPEG Surround 2-1-2 tools (ISO/IEC 23003-3).";
    public const string Mps212Config = "Mps212Config: the MPEG Surround 2-1-2 settings of a channel pair element whose stereoConfigIndex is 1 to 3, which rebuild the pair from a downmix and parameters (ISO/IEC 23003-3 5.2).";
    public const string MpsFreqRes = "bsFreqRes: the frequency resolution of the MPEG Surround parameters, as a number of parameter bands: 28, 20, 14, 10, 7, 5 or 4 for 1 to 7; 0 is reserved (ISO/IEC 23003-3 5.2, ISO/IEC 23003-1).";
    public const string MpsFixedGainDmx = "bsFixedGainDMX: the fixed gain of the downmix, as an index (ISO/IEC 23003-3 5.2, ISO/IEC 23003-1).";
    public const string MpsTempShapeConfig = "bsTempShapeConfig: which temporal shaping the upmix applies to its decorrelated signals; with 2, envelope reshaping, whose quantization mode follows (ISO/IEC 23003-3 5.2, ISO/IEC 23003-1).";
    public const string MpsDecorrConfig = "bsDecorrConfig: the configuration of the decorrelator, 0 to 2; 3 is reserved (ISO/IEC 23003-3 5.2).";
    public const string MpsHighRateMode = "bsHighRateMode: whether each frame codes its own framing and smoothing of the parameters; without it, every frame has one parameter set at fixed framing (ISO/IEC 23003-3 5.2).";
    public const string MpsPhaseCoding = "bsPhaseCoding: whether phase differences between the two channels are coded (ISO/IEC 23003-3 5.2).";
    public const string MpsOttBandsPhasePresent = "bsOttBandsPhasePresent: whether the number of parameter bands with phase differences follows; otherwise a default for the frequency resolution applies (ISO/IEC 23003-3 5.2).";
    public const string MpsOttBandsPhase = "bsOttBandsPhase: the number of parameter bands with phase differences (ISO/IEC 23003-3 5.2).";
    public const string MpsResidualBands = "bsResidualBands: the number of parameter bands for which the second core channel carries a residual signal, with stereoConfigIndex 2 or 3 (ISO/IEC 23003-3 5.2).";
    public const string MpsPseudoLr = "bsPseudoLr: whether the core codes the downmix and residual as a pseudo left/right pair, which the decoder turns back into downmix and residual before the upmix (ISO/IEC 23003-3 5.2, 7.11.2.6).";
    public const string MpsEnvQuantMode = "bsEnvQuantMode: the quantization of the envelope reshaping data, with bsTempShapeConfig 2 (ISO/IEC 23003-3 5.2, ISO/IEC 23003-1).";
    public const string ExtElementType = "usacExtElementType: what the extension element carries, such as 3 for audio pre-roll, the configuration and earlier frames a decoder needs to start at an independent frame (ISO/IEC 23003-3 5.2).";
    public const string ExtConfigLength = "usacExtElementConfigLength: the length of the extension's configuration in bytes (ISO/IEC 23003-3 5.2).";
    public const string HasDefaultLength = "usacExtElementDefaultLengthPresent: whether a default payload length follows (ISO/IEC 23003-3 5.2).";
    public const string DefaultLength = "The default length of the extension's payload in bytes (ISO/IEC 23003-3 5.2).";
    public const string PayloadFragmentation = "usacExtElementPayloadFrag: whether the extension's payload may be split over frames (ISO/IEC 23003-3 5.2).";
    public const string HasConfigExtension = "usacConfigExtensionPresent: whether configuration extensions follow, such as loudness information (ISO/IEC 23003-3 5.2).";
    public const string UsacConfigExtension = "The configuration extensions: data about the stream that is not part of its coding, such as loudness information (ISO/IEC 23003-3 5.2).";
    public const string ConfigExtensionCount = "numConfigExtensions: how many configuration extensions follow (ISO/IEC 23003-3 5.2).";
    public const string ConfigExtensionType = "usacConfigExtType: 0 fill, 2 loudness information, 7 a stream ID (ISO/IEC 23003-3 5.2).";
    public const string ConfigExtensionLength = "usacConfigExtLength: the extension's length in bytes (ISO/IEC 23003-3 5.2).";

    public const string PceTag = "element_instance_tag: the number of this program config element (ISO/IEC 14496-3 4.4.1.1).";
    public const string PceObjectType = "object_type: the profile of the program, the audio object type minus one (ISO/IEC 14496-3 4.4.1.1).";
    public const string PceSampleRate = "sampling_frequency_index: the program's sample rate (ISO/IEC 14496-3 4.4.1.1).";
    public const string FrontElements = "num_front_channel_elements: how many channel elements feed front speakers (ISO/IEC 14496-3 4.4.1.1).";
    public const string SideElements = "num_side_channel_elements: how many channel elements feed side speakers (ISO/IEC 14496-3 4.4.1.1).";
    public const string BackElements = "num_back_channel_elements: how many channel elements feed back speakers (ISO/IEC 14496-3 4.4.1.1).";
    public const string LfeElements = "num_lfe_channel_elements: how many LFE elements the program has (ISO/IEC 14496-3 4.4.1.1).";
    public const string DataElements = "num_assoc_data_elements: how many data stream elements belong to the program (ISO/IEC 14496-3 4.4.1.1).";
    public const string CouplingElements = "num_valid_cc_elements: how many coupling channel elements the program uses (ISO/IEC 14496-3 4.4.1.1).";
    public const string HasMonoMixdown = "mono_mixdown_present: whether a separate mono downmix element is named (ISO/IEC 14496-3 4.4.1.1).";
    public const string MonoMixdown = "The tag of the single channel element that holds a mono downmix (ISO/IEC 14496-3 4.4.1.1).";
    public const string HasStereoMixdown = "stereo_mixdown_present: whether a separate stereo downmix element is named (ISO/IEC 14496-3 4.4.1.1).";
    public const string StereoMixdown = "The tag of the channel pair element that holds a stereo downmix (ISO/IEC 14496-3 4.4.1.1).";
    public const string HasMatrixMixdown = "matrix_mixdown_idx_present: whether a coefficient for mixing the surround channels down to stereo follows (ISO/IEC 14496-3 4.4.1.1).";
    public const string MatrixMixdown = "matrix_mixdown_idx: the weight of the surround channels in a stereo downmix (ISO/IEC 14496-3 4.4.1.1).";
    public const string PseudoSurround = "pseudo_surround_enable: whether the stereo downmix may be decoded as pseudo surround (ISO/IEC 14496-3 4.4.1.1).";
    public const string PceChannelElement = "A channel element of the program: whether it is a pair and its instance tag (ISO/IEC 14496-3 4.4.1.1).";
    public const string IsChannelPair = "Whether the element is a channel pair element, two channels, or a single channel element (ISO/IEC 14496-3 4.4.1.1).";
    public const string ElementTag = "The instance tag of the element, which the access units' elements carry (ISO/IEC 14496-3 4.4.1.1).";
    public const string LfeTag = "The instance tag of an LFE element of the program (ISO/IEC 14496-3 4.4.1.1).";
    public const string DataTag = "The instance tag of a data stream element of the program (ISO/IEC 14496-3 4.4.1.1).";
    public const string CouplingElement = "A coupling channel element of the program (ISO/IEC 14496-3 4.4.1.1).";
    public const string IndependentlySwitched = "cc_element_is_ind_sw: whether the coupling element is applied independently of the channels' block switching (ISO/IEC 14496-3 4.4.1.1).";
    public const string CommentLength = "comment_field_bytes: the length of the comment (ISO/IEC 14496-3 4.4.1.1).";
    public const string Comment = "The program's comment text (ISO/IEC 14496-3 4.4.1.1).";

    public const string Stream = "The AAC stream the container holds: its access units, one per sample of the track, with the statistics the analyzer computed over them.";
    public const string AccessUnit = "One access unit: the coded audio of one frame, 1024 or 960 samples per channel at the core's rate. For AAC it is a raw data block of syntax elements (ISO/IEC 14496-3 4.4.2.1).";
    public const string UsacAccessUnit = "One USAC access unit, a frame of the coded audio (ISO/IEC 23003-3 5.3).";
    public const string IsIndependent = "usacIndependencyFlag: whether the frame can be decoded without the frames before it, so playback can start here (ISO/IEC 23003-3).";
    public const string Element = "id_syn_ele: the syntax element that opens the raw data block: SCE a single channel, CPE a channel pair, LFE, CCE coupling, DSE data, PCE program config, FIL fill (ISO/IEC 14496-3 4.4.2.1).";
    public const string ElementInstanceTag = "element_instance_tag: which element of its kind this is, so a program config element can place it (ISO/IEC 14496-3 4.4.2.1).";
    public const string FillCount = "The fill element's count: its payload length in bytes, where 15 means a further byte adds more (ISO/IEC 14496-3, fill_element).";
    public const string FillEscapeCount = "esc_count: the fill element's payload length beyond 14 bytes (ISO/IEC 14496-3, fill_element).";
    public const string FillExtensionType = "extension_type: what the fill element carries, such as 13 or 14 for SBR data, 11 for dynamic range control, 0 or 1 for plain filler (ISO/IEC 14496-3, fill_element).";
    public const string ElementText = "Readable text in the element's payload, such as an encoder's name and version.";

    public const string Adts = "A run of ADTS frames: raw AAC with a header before every frame, so a decoder can start anywhere and needs no container (ISO/IEC 14496-3 1.A.2.2; ISO/IEC 13818-7 6.2).";
    public const string UnreadFrames = "The bytes after the frames the analyzer counted past its limit of 2,097,152 frame records, where the stream's frames do not continue, another stream starts, or the 4,096 sync losses it follows are used up; they are not analyzed.";

    public const string CountedFrames = "ADTS frames past the analyzer's limit of 2,097,152 frame records, counted from their headers in the stream's frames, samples, duration and bitrates; they have no node of their own and their raw data blocks are not read.";

    public const string UnreadBlocks = "The bytes after the raw data blocks the analyzer counted past its limit of 2,097,152 block records, which cannot be read to the end of a block and are not analyzed.";

    public const string CountedBlocks = "Raw data blocks past the analyzer's limit of 2,097,152 block records, read only for their length and counted in the stream's blocks, samples, duration and bitrates; they have no node of their own.";

    public const string Gap = "Bytes between ADTS frames that are not a frame: data before the first frame, where sync was lost, or after the last frame.";
    public const string AdtsFrame = "One ADTS frame: a 7-byte header, a CRC where it is protected, then one to four raw data blocks of 1024 samples per channel each (ISO/IEC 14496-3 1.A.2.2).";
    public const string SyncWord = "syncword: 12 set bits that start every ADTS header (ISO/IEC 14496-3 1.A.2.2.1).";
    public const string AdtsVersion = "ID: 0 for MPEG-4 AAC, 1 for MPEG-2 AAC, as ISO/IEC 13818-7 defines it (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsLayer = "layer: always 0 in ADTS, which keeps its headers apart from those of MPEG audio, whose layer is never 0 (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsProtected = "Whether a CRC follows the header; the stored protection_absent bit is 0 when it does (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsProfile = "profile_ObjectType: the audio object type minus one, so only AAC Main, LC, SSR and LTP fit. HE-AAC shows as LC, its core (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsSampleRate = "sampling_frequency_index: the core codec's sample rate. HE-AAC states the core's half rate here (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsPrivate = "private_bit: left to the application; decoders ignore it (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsChannelConfiguration = "channel_configuration: the channel layout by number, 1 mono, 2 stereo; 0 leaves it to a program config element in the raw data block (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsOriginal = "original_copy: set for an original, clear for a copy; encoders set it as they are configured (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsHome = "home: a bit the specification leaves to the application (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string CopyrightIdBit = "copyright_identification_bit: one bit of a 72-bit copyright identifier that the frames carry one bit at a time (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string CopyrightIdStart = "copyright_identification_start: whether this frame's bit is the first of the copyright identifier (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string AdtsFrameLength = "aac_frame_length: the frame's length in bytes, header included, which gives where the next frame starts (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string BufferFullness = "adts_buffer_fullness: how full the decoder's input buffer is after this frame, in 32-bit words per channel, so a decoder can start mid-stream. 0x7FF marks a variable bitrate (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string RawDataBlockCount = "number_of_raw_data_blocks_in_frame plus one: how many raw data blocks of 1024 samples the frame holds (ISO/IEC 14496-3 1.A.3.2.1).";
    public const string RawDataBlockPosition = "raw_data_block_position: where a raw data block after the first starts, in bytes from the start of the first raw data block, which follows the header, the positions and the header CRC (ISO/IEC 14496-3 1.A.3.2.2).";
    public const string Crc = "The CRC-16 the encoder stored over the header and the first bits of each channel element, so decoders can detect damage (ISO/IEC 14496-3 1.A.2.2.2, 1.A.4.3).";
    public const string BlockCrc = "The CRC-16 of the raw data block before it, which a frame of several protected blocks stores after each (ISO/IEC 14496-3 1.A.2.2.2).";
    public const string CrcCheck = "What the analyzer found when it computed the CRC over the same bits. Checking a channel element needs to know where it ends, which takes its spectral data.";
    public const string RawDataBlock = "One raw data block: the syntax elements of one frame, 1024 samples per channel, ending with an END element (ISO/IEC 14496-3 4.4.2.1).";

    public const string Adif = "An ADIF stream: one header at the start of the file, then raw data blocks with no headers or lengths between them (ISO/IEC 14496-3 1.A.2.1; ISO/IEC 13818-7 6.1).";
    public const string AdifHeader = "The ADIF header: copyright, bitrate and the program config elements that give the channel layout (ISO/IEC 14496-3 1.A.2.1).";
    public const string AdifId = "adif_id: the four letters ADIF that start the file (ISO/IEC 14496-3 1.A.2.1).";
    public const string HasCopyrightId = "copyright_id_present: whether a 72-bit copyright identifier follows (ISO/IEC 14496-3 1.A.2.1).";
    public const string CopyrightId = "copyright_id: a 72-bit copyright identifier, shown in two parts (ISO/IEC 14496-3 1.A.2.1).";
    public const string AdifOriginal = "original_copy: set for an original, clear for a copy (ISO/IEC 14496-3 1.A.2.1).";
    public const string AdifHome = "home: a bit the specification leaves to the application (ISO/IEC 14496-3 1.A.2.1).";
    public const string BitstreamType = "bitstream_type: 0 for a constant bitrate, 1 for a variable one (ISO/IEC 14496-3 1.A.2.1).";
    public const string AdifBitrate = "bitrate: the constant bitrate, or for a variable bitrate the peak (ISO/IEC 14496-3 1.A.2.1).";
    public const string PceCount = "num_program_config_elements plus one: how many programs the stream defines (ISO/IEC 14496-3 1.A.2.1).";
    public const string AdifBufferFullness = "adif_buffer_fullness: how full the decoder's buffer is after the first raw data block, for a constant bitrate (ISO/IEC 14496-3 1.A.2.1).";
    public const string AdifBlocks = "The raw data blocks after the header, back to back without lengths; finding their bounds needs every element parsed (ISO/IEC 14496-3 1.A.2.1).";
    public const string AdifUnread = "The bytes after the longest extent of a raw data block that could not be read to its end: ADIF gives no block's length or start, so the blocks after one that breaks cannot be found (ISO/IEC 14496-3 1.A.2.1).";

    public const string Statistics = "Figures the analyzer computed over the stream's access units or frames.";
    public const string StatUnits = "How many access units or frames the stream has.";
    public const string StatSamples = "Samples per channel at the output rate, before any gapless trimming: the access units times the samples each one decodes to.";
    public const string StatDuration = "The playing time of those samples, before any gapless trimming.";
    public const string AverageBitrate = "The bits of the access units divided by their playing time; container overhead and ADTS headers aside, the stream's bitrate.";
    public const string Smallest = "The size of the smallest access unit or frame.";
    public const string Largest = "The size of the largest access unit or frame.";
    public const string MinimumBitrate = "The lowest bitrate of an access unit or frame: its bits over its playing time.";
    public const string MaximumBitrate = "The highest bitrate of an access unit or frame: its bits over its playing time.";
    public const string FirstElements = "How many access units start with each syntax element. Encoders that sign their streams put a fill element first.";
    public const string FirstElementCount = "The access units that start with this syntax element (ISO/IEC 14496-3 4.4.2.1).";
    public const string IndependentUnits = "How many USAC access units can be decoded without the ones before them.";

    public const string Encoder = "Which program encoded the stream, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: high when the encoder signed the stream itself, low when only text in the container's metadata, which other software may have written, names it.";
    public const string Evidence = "What points to the encoder, at the bytes it was found in.";

    public const string BitrateSeries = "The bitrate of each access unit or frame: its bits over its playing time.";
    public const string BitrateOverTimeSeries = "The bitrate of each second of audio: the bits of the access units that start in it over their playing time. A stream of more than about 36 hours, or one stated at a very low sample rate, is graphed in spans of 2, 4 or more seconds.";

    // The value of an encoder node, by its name.
    public static string EncoderNode(string name) => name switch
    {
        "Encoder" => Encoder,
        "Confidence" => Confidence,
        _ => Evidence,
    };

    // The statistics figures, by their name, as StreamFigures names them.
    public static string Figure(string name) => name switch
    {
        "Statistics" => Statistics,
        "Access Units" or "Frames" => StatUnits,
        "Samples" => StatSamples,
        "Duration" => StatDuration,
        "Average Bitrate" => AverageBitrate,
        "Smallest Access Unit" or "Smallest Frame" => Smallest,
        "Largest Access Unit" or "Largest Frame" => Largest,
        "Minimum Bitrate" => MinimumBitrate,
        _ => MaximumBitrate,
    };
}

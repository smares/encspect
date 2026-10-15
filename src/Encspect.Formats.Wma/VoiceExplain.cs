namespace Encspect.Formats.Wma;

// What each Windows Media Audio Voice node is. Constants only, shared by every node of their kind, written from FFmpeg 6.1.1's
// decoder (libavcodec/wmavoice.c); Microsoft has published no specification of the codec.
internal static class VoiceExplain
{
    public const string ProfessionalHeader = "The first 18 bytes of the codec-specific data. The comment in wmavoice.c calls them the extradata of Windows Media Audio Professional, which music superframes inside Voice use (\"WMAPro-in-WMAVoice\"); FFmpeg's decoder does not read them.";
    public const string Flags = "The 32-bit flags word (bytes 18 to 21, little-endian) that turns the codec's options on: FFmpeg's decoder reads the postfilter (bit 0), the denoise strength (bits 2 to 5), the tilt correction (bit 6), the DC level (bits 7 to 10), the LSP count (bit 12), the quantizer mode (bit 13) and the default mode (bit 14) (wmavoice.c: wmavoice_decode_init). The comment there says bytes 19 to 22; the code reads byte 18.";
    public const string FlagPostfilter = "Whether the decoder applies the averaged projection filter (APF), the postfilter, to its output (wmavoice.c: do_apf). The postfilter reads nothing from the frames.";
    public const string FlagDenoiseStrength = "The strength of the Wiener filter that denoises the output in the postfilter, 0 to 11; FFmpeg's decoder rejects 12 and more (wmavoice.c: denoise_strength).";
    public const string FlagTiltCorrection = "Whether the postfilter applies tilt correction to the Wiener filter's coefficients (wmavoice.c: denoise_tilt_corr).";
    public const string FlagDcLevel = "The predicted amount of DC noise, 0 to 15. A decoder removes ultra-low-frequency noise with a high-pass filter from 9 on (wmavoice.c: dc_level, postfilter).";
    public const string FlagLspCount = "The number of line spectral pairs (LSPs) per frame: 16 if the flag is set, else 10 (wmavoice.c: lsps). It sets how many bits the LSPs take.";
    public const string FlagLspQuantizerMode = "Selects, for residual LSPs, which of two sets of interpolation coefficients is used (wmavoice.c: lsp_q_mode).";
    public const string FlagLspDefaultMode = "Selects which of two sets of mean LSP values the LSPs are offset from (wmavoice.c: lsp_def_mode).";
    public const string FlagUnread = "A bit of the flags that FFmpeg's decoder does not read.";
    public const string FlagOthers = "Bits of the flags that FFmpeg's decoder does not read.";
    public const string FrameTypeTable = "The table that maps the codes of the frame types to the 17 frame types: for each type a group of 3 bits, 17 in all, from byte 22 (wmavoice.c: decode_vbmtree). The decoder gives the codes of group 0 two bits, of group 1 four, and so on to ten bits for group 4, twelve for group 5 and fourteen for groups 6 and 7.";
    public const string FrameTypeGroup = "The code length of one frame type. The table gives the type a group of 3 bits, which with the types before it in the group sets its slot, and the slot sets which of the 22 codes is its; a type in a slot that no code reaches has none (wmavoice.c: decode_vbmtree, wmavoice_init_static_data).";
    public const string FrameTypeTableUnused = "The bits of the table after the 17 groups, which FFmpeg's decoder does not read; the comment in wmavoice.c says they are 0.";
    public const string ExtraData = "Bytes after the fields the format defines, which decoders ignore.";

    public const string Stream = "The Windows Media Audio Voice stream: its packets, which the container carries as media objects, and the superframes in them.";
    public const string Packets = "The packets in the order they are stored: blocks of the block alignment's size, each with a header, the end of the superframe before, whole superframes and the start of one that goes on.";
    public const string Superframes = "The superframes in decoding order: each holds three frames of 160 samples, or fewer samples in all where its header says so. A superframe that spans two packets lies in the bytes of both.";

    public const string Packet = "A packet: a block of the block alignment's size with a header, the bits that finish the superframe before, whole superframes and the start of the one that goes on (wmavoice.c: wmavoice_decode_packet).";
    public const string PacketSequence = "The number of the packet, counted from 0 to 15 and around. FFmpeg's decoder skips it (wmavoice.c: parse_packet_header).";
    public const string PacketResidual = "Whether the superframes of the packet send one set of LSPs for all three frames (residual coding), or each frame its own (wmavoice.c: has_residual_lsps).";
    public const string PacketSuperframes = "How many superframes begin in the packet, not counting the one that began in the packet before: a sum of 6-bit values, each 63 saying that another follows. The last of them goes on to the end of the packet and is finished by the next packet's spillover bits (wmavoice.c: parse_packet_header).";
    public const string PacketSpillover = "How many bits after the header finish the superframe that began in the packet before. The field is 3 bits wider than the base-2 logarithm of the block alignment, rounded up (wmavoice.c: spillover_bitsize).";
    public const string PacketSuperframeEnd = "The last bits of the superframe that began in the packet before.";
    public const string PacketSkippedSpillover = "Spillover bits that a decoder skips, since no start of a superframe is left from the packet before (wmavoice.c: wmavoice_decode_packet).";
    public const string PacketSuperframe = "The bits of a superframe that lies in this packet.";
    public const string PacketSuperframeStart = "The first bits of a superframe that goes on in the next packet.";
    public const string PacketUnfinished = "The first bits of a superframe that no packet finishes: the stream ends, a media object with no header drops it, or the superframe needs more bits than there are.";
    public const string PacketPadding = "Bits that a superframe's reading did not use, between its end and the end of the span the packets give it. A decoder ignores them.";
    public const string PacketUnread = "Bits of the packet that the reading did not take: after the last superframe the header counts, or after a superframe that failed, which makes a decoder drop the rest of the media object.";
    public const string PacketUnreadResync = "Bits in front of the packet's own superframes that a decoder's resync skips, a few more than the spillover, after a spillover that did not finish its superframe.";
    public const string PacketUnreadCarried = "Bits at the start of a block with no header that the reading of the superframe before went on into, which a decoder skips.";
    public const string PacketUnreadSkipKept = "Bits at the start of a media object with no header that a decoder skips because an earlier packet header it rejected left them to skip (wmavoice.c: skip_bits_next); no superframe read them.";
    public const string PacketUnreadHeader = "Bits after a packet header that runs past the packet: a decoder rejects the header and drops the rest of the media object.";
    public const string PacketUnreadCache = "Bits that a decoder's cache did not take, as they did not fit in it or the spillover ran past the packet. The decoder reads the bytes the cache held before in their place.";

    public const string Superframe = "A superframe: a header, three frames of 160 samples and optional additional data (wmavoice.c: synth_superframe).";
    public const string SuperframeFromCache = "A superframe: a header, three frames of 160 samples and optional additional data (wmavoice.c: synth_superframe). Some or all of its bits were not copied from the file into the decoder's cache, and a decoder reads the bytes the cache held before in their place: the fields read there have a value and no bytes.";
    public const string SpeechFlag = "The first bit of a superframe: set for speech, which the codec reads; clear for music, Windows Media Audio Professional data that FFmpeg's decoder does not support (wmavoice.c: synth_superframe).";
    public const string HasSampleCount = "A flag that the superframe states how many samples it holds. Without it, it holds 480.";
    public const string SampleCount = "How many samples the superframe holds, at most 480 (wmavoice.c: synth_superframe). The comment there says it is usually used to prevent blanks at track boundaries.";
    public const string SuperframeLsps = "The LSPs of all three frames, sent once: a first set coded on its own and values that interpolate between it and the last frame of the superframe before, 48 bits for 10 LSPs and 60 for 16 (wmavoice.c: dequant_lsp10r, dequant_lsp16r).";
    public const string AdditionalData = "An optional flag and, when it is set, a 4-bit count and that many plus one groups of 10 bits, which FFmpeg's decoder skips (wmavoice.c: synth_superframe).";
    public const string UnreadBits = "Bits of a superframe after the point where its reading stopped.";

    public const string Frame = "A frame: 160 samples, divided by its type into 1 to 8 blocks. It holds its type and, unless the superframe sends them once, its LSPs, then the pitch and the excitation (wmavoice.c: synth_frame).";
    public const string FrameLsps = "The LSPs of the frame, coded on their own: 24 bits for 10, 34 for 16 (wmavoice.c: dequant_lsp10i, dequant_lsp16i). A decoder turns them into the filter that shapes the excitation.";
    public const string FrameType = "The type of the frame: a code of 2 to 14 bits that the frame type table of the codec data maps to one of 17 types. The type sets the blocks per frame, the adaptive codebook and the fixed codebook (wmavoice.c: frame_descs).";
    public const string FramePitch = "The pitch of the frame in samples, the delay of its adaptive codebook, sent once for the types with a pitch per frame; a decoder interpolates it from the last frame's pitch to give each block its own (wmavoice.c: synth_frame). The field is as wide as the sample rate's range of pitches needs.";
    public const string SilenceGain = "The index (8 bits) of the gain of a silence frame's comfort noise (wmavoice.c: synth_frame).";
    public const string WindowIndex = "Where the first pulse of the pitch-adaptive window lies, an index of 6 bits, or 8 for values from 54 (wmavoice.c: aw_parse_coords).";
    public const string Blocks = "The blocks of the frame, each with its own excitation. Their fields are read to find where the frame ends and are not reported.";
    public const string Block = "A block: the frame's 160 samples divided by its type's block count. It holds its pitch (for the types with a pitch per block), the fixed codebook's fields and a gain index (wmavoice.c: synth_block).";

    public const string Statistics = "Figures over the stream's superframes, counted while reading them.";
    public const string StatPackets = "How many packets the stream has.";
    public const string StatSuperframes = "How many superframes were read to their end.";
    public const string StatInvalidSuperframes = "Superframes that could not be read to their end.";
    public const string StatFrames = "How many frames the superframes hold.";
    public const string StatSamples = "The samples a decoder outputs: the samples the superframes state, 480 for each unless the header says fewer.";
    public const string StatDuration = "The time those samples last.";
    public const string StatSuperframeBitrate = "The bits the superframes took over the time their samples last, also those a decoder reads from its cache's old bytes; the packets' headers and the bits no superframe uses are not counted.";
    public const string StatSmallestSuperframe = "The fewest bytes a superframe takes.";
    public const string StatLargestSuperframe = "The most bytes a superframe takes.";
    public const string StatMinimumSuperframeBitrate = "The bitrate of the smallest superframe.";
    public const string StatMaximumSuperframeBitrate = "The bitrate of the largest superframe.";
    public const string StatStraddling = "Superframes that begin in one packet and end in the next.";
    public const string StatMaxPerPacket = "The most superframes that end in one packet.";
    public const string StatSampleCount = "Superframes that state how many samples they hold.";
    public const string StatResidual = "Superframes that send one set of LSPs for their three frames.";
    public const string StatAdditionalData = "Superframes that carry additional data, which a decoder skips.";
    public const string StatIgnoredBits = "Bits that the reading of a superframe did not use, between its end and the end of the span the packets give it.";
    public const string StatEndPadding = "Bits at the end of the last packet that no superframe took from the file: padding after the last superframe, or the start of a superframe that did not fit a decoder's cache.";
    public const string StatSkippedSpillover = "Spillover bits that no superframe start belonged to.";
    public const string StatLspCount = "The line spectral pairs of each frame, from the flags.";
    public const string StatSpilloverBits = "The width of the spillover field in a packet's header.";
    public const string StatPostfilter = "Whether the flags turn the decoder's postfilter on.";
    public const string StatFrameLength = "The samples each frame holds.";
    public const string StatPitchRange = "The smallest and largest pitch in samples that the sample rate allows, which set how wide the pitch fields are.";
    public const string StatFrameTypes = "How many frames there are of each type.";
    public const string StatFrameType = "The share of the frames of one type.";
    public const string StatFixedCodebooks = "How many frames use each kind of fixed codebook.";
    public const string StatAdaptiveCodebooks = "How many frames use each kind of adaptive codebook.";
    public const string StatBlocksPerFrame = "How many frames are divided into each number of blocks.";
    public const string StatCodebookShare = "The share of the frames that use one kind of codebook, or one number of blocks.";
}

internal static class VoiceSeriesText
{
    public const string Bitrate = "The bitrate of each superframe: its bits over the time its samples last.";
    public const string BitrateOverTime = "The bitrate in each second of audio: the bits of the superframes that start in it over their duration.";
}

// The words for the frame types, written from wmavoice.c's frame_descs: what each type's codebooks are.
internal static class VoiceNames
{
    public static readonly string[] FixedCodebooks = ["silence (comfort noise)", "hardcoded codebook", "pitch-adaptive window pulses", "pulse sets"];

    public static readonly string[] AdaptiveCodebooks = ["no adaptive codebook", "pitch per frame", "pitch per block"];

    public static string Frame(int type)
    {
        byte[] d = VoiceTables.FrameDescriptors[type];
        string blocks = d[0] == 1 ? "1 block" : $"{d[0]} blocks";
        string pulses = d[3] == 3
            ? d[4] switch
            {
                0 => ", 5 single pulses",
                5 => ", 5 pulse pairs",
                _ => $", {d[4]} pulse pairs and {5 - d[4]} single pulses",
            }
            : string.Empty;
        return $"{FixedCodebooks[d[3]]}, {blocks}, {AdaptiveCodebooks[d[2]]}{pulses}";
    }
}

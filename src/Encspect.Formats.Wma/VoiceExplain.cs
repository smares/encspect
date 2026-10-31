namespace Encspect.Formats.Wma;

// The names of the frame types and the codebooks, as nodes and statistics show them.
internal static class VoiceNames
{
    public static readonly string[] FixedCodebooks = ["silence (comfort noise)", "hardcoded codebook", "pitch-adaptive window pulses", "pulse sets"];

    public static readonly string[] AdaptiveCodebooks = ["no adaptive codebook", "pitch per frame", "pitch per block"];

    // The text of a frame type after "Type t: ": its fixed codebook, blocks, adaptive codebook and, for pulse sets, the pulses.
    public static string Frame(int type)
    {
        byte[] d = VoiceTables.FrameDescriptors[type];
        string blocks = d[0] == 1 ? "1 block" : $"{d[0]} blocks";
        string name = $"{FixedCodebooks[d[3]]}, {blocks}, {AdaptiveCodebooks[d[2]]}";
        if (d[3] == 3)
        {
            name += d[4] switch
            {
                0 => ", 5 single pulses",
                5 => ", 5 pulse pairs",
                _ => $", {d[4]} pulse pairs and {5 - d[4]} single pulses",
            };
        }

        return name;
    }

    public static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}

// What each Windows Media Audio Voice node is. Constants only, shared by every node of their kind.
internal static class VoiceExplain
{
    public const string Stream = "The stream as its packets, superframes and frames read: Windows Media Audio Voice codes speech at low bitrates in superframes of 480 samples, each of three frames.";
    public const string Statistics = "Figures over the stream's packets, superframes and frames, counted from what the analysis read.";

    public const string StatPackets = "How many packets the stream has, read or not.";
    public const string StatPacketBytes = "The bytes of all the packets.";
    public const string StatBlockAlignment = "The size of each packet, the container's block alignment.";
    public const string StatAverageBitrate = "The packets' bytes over the duration.";
    public const string StatContainerDuration = "The length of the stream as the container states it, the play duration less the preroll.";
    public const string StatNominalBitrate = "The average bytes per second the container states for the stream (the WAVEFORMATEX's nAvgBytesPerSec), as a bitrate. The packets cover more than the duration where the stream starts with a preroll, so the packets' bytes over the duration would overstate it.";
    public const string StatSuperframes = "How many superframes were read: those that did not fail.";
    public const string StatInvalidSuperframes = "How many superframes failed: music, more than 480 samples, a frame type code with no type, or more bits than the packets give.";
    public const string StatFrames = "How many frames the superframes read hold: three each.";
    public const string StatFrameLength = "The samples in each frame.";
    public const string StatSamples = "The samples the superframes read output: 480 each, or the count a superframe states.";
    public const string StatDuration = "The samples over the sample rate.";
    public const string StatAverageSuperframeBitrate = "The bits of the superframes read over the duration, without the packet headers, the spillover fields and the bits no superframe uses.";
    public const string StatSmallestSuperframe = "The fewest bytes a superframe read takes, from its bits.";
    public const string StatLargestSuperframe = "The most bytes a superframe read takes, from its bits.";
    public const string StatMinimumBitrate = "The lowest bitrate of a superframe read: its bits over the 480 samples a superframe holds.";
    public const string StatMaximumBitrate = "The highest bitrate of a superframe read: its bits over the 480 samples a superframe holds.";
    public const string StatSpanning = "How many superframes continue from one packet into the next: those read from the bits cached at a header.";
    public const string StatMostInPacket = "The most superframes read in one packet, counting the one that began in the packet before and the packet finishes.";
    public const string StatSpilloverField = "The width of the spillover field in each packet header, which follows from the block alignment.";
    public const string StatSampleCount = "How many superframes state their own sample count.";
    public const string StatLspsPerFrame = "How many line spectral pairs describe the filter of each frame: 10 or 16, as the codec-specific data's flags say.";
    public const string StatResidualLsps = "The superframes read with their LSPs sent once for the three frames, as the packet's header says.";
    public const string StatAdditionalData = "How many superframes carry additional data, which a decoder skips.";
    public const string StatPostfilter = "Whether the flags of the codec-specific data switch the postfilter on, a filter a decoder applies to the audio.";
    public const string StatPitchRange = "The shortest and longest pitch a frame can send, in samples, which follow from the sample rate.";
    public const string StatIgnoredBits = "Bits of a superframe's span in the packets that it did not use, where it used only bits from the file.";
    public const string StatBitsAfterLast = "Bits at the end of the stream after the last superframe, or the bits of a last start that could not be finished.";
    public const string StatSkippedSpillover = "Spillover bits that finish no superframe: the packet before ended in none that is still cached.";
    public const string StatFrameTypes = "How many frames each of the 17 frame types has.";
    public const string StatFixedCodebooks = "How many frames use each kind of fixed codebook, the part of the excitation that a frame's pulses code.";
    public const string StatAdaptiveCodebooks = "How many frames use each kind of adaptive codebook, the part of the excitation that repeats the pitch of the sound before.";
    public const string StatBlocksPerFrame = "How many frames are divided into 1, 2, 4 or 8 blocks, each with its own excitation.";
    public const string StatFrameTypeShare = "The frames of this type, with their share of all the frames read.";
    public const string StatFixedShare = "The frames that use this fixed codebook, with their share of all the frames read.";
    public const string StatAdaptiveShare = "The frames that use this adaptive codebook, with their share of all the frames read.";
    public const string StatBlocksShare = "The frames of this many blocks, with their share of all the frames read.";

    public const string Packets = "The packets of the stream, each as many bytes as the block alignment says. A packet has a header, spillover that finishes the superframe the packet before ended in, superframes, and the start of the one it ends in.";
    public const string Packet = "A unit of the block alignment's size inside a media object. Its header counts the superframes that begin in it; the last of them continues into the next packet.";
    public const string PacketSequenceNumber = "A number that counts up by one from packet to packet, modulo 16. FFmpeg's decoder does not read it.";
    public const string HasResidualLsps = "Whether the superframes read with this header send their LSPs once for all three frames (the residual form) instead of in every frame.";
    public const string SuperframesInPacket = "How many superframes begin in the packet: the sum of the 6-bit values of the header, where a value of 63 means that another follows. The last one continues into the next packet.";
    public const string SpilloverBits = "How many bits after the header finish the superframe that the packet before ended in.";
    public const string SuperframeEnd = "The spillover bits that the superframe from the packet before used to its end.";
    public const string Padding = "Bits that a superframe did not use. A decoder ignores them and goes on at the position the next packet's header states.";
    public const string SkippedSpillover = "Spillover bits for which no start is left, the start in the packet before having been lost or never cached. A decoder skips them.";
    public const string SuperframeData = "A superframe read in this packet, with its three frames.";
    public const string SuperframeStart = "The bits of this packet that begin the superframe it ends in; the next packet's spillover finishes it.";
    public const string UnfinishedSuperframe = "The bits of this packet that begin a superframe that was never finished: no packet followed to finish it, or the superframe failed.";
    public const string UnreadBits = "Bits of the packet that the walk did not read: bits a late resync skipped, bits after a failure or after the superframes the header counted, or bits the decoder's cache refused.";

    public const string Superframes = "The superframes the analysis read, each of three frames and 480 samples or fewer.";
    public const string Superframe = "480 samples of speech in three frames of 160. A superframe states no size: it ends where its last frame and its additional data end.";
    public const string IsSpeech = "Whether the superframe holds speech. A clear bit means music, Windows Media Audio Professional data inside the superframe, which FFmpeg's decoder does not support.";
    public const string HasSampleCount = "Whether the superframe states how many samples it outputs; without a count it outputs 480.";
    public const string SampleCount = "How many samples the superframe outputs, 0 to 480. It does not say where the superframe ends.";
    public const string Lsps = "The line spectral pairs that describe the filter of the sound. Their values are not decoded.";
    public const string Frame = "160 samples of speech. Its type sets how many blocks it has and how its excitation is coded.";
    public const string FrameType = "The type of the frame, one of 17, sent as a code of 2 to 14 bits that the codec-specific data's frame type table maps to the type.";
    public const string Pitch = "The pitch of the frame in samples, the delay at which the sound repeats.";
    public const string SilenceGain = "The index of the gain of the comfort noise that a silence frame stands for.";
    public const string WindowIndex = "The index that places the first pulse of a pitch-adaptive window frame: 6 bits, or 8 where the first six are 54 or more.";
    public const string Blocks = "The blocks of the frame, equal parts of 1, 2, 4 or 8 that each carry their own excitation.";
    public const string Block = "One block of the frame: its pitch field where the frame has a pitch per block, and its excitation fields. Their values are not decoded.";
    public const string AdditionalData = "A flag, and where it is set a count and groups of 10 bits that a decoder skips.";

    public const string ProfessionalHeader = "The first 18 bytes of the codec-specific data, laid out as Windows Media Audio Professional's. FFmpeg's decoder does not read them.";
    public const string Flags = "The flags of the codec-specific data (bytes 18 to 21): the postfilter, the denoise settings and the LSP form, as FFmpeg's decoder reads them.";
    public const string FlagPostfilter = "Whether the decoder's postfilter is on. It shapes the audio only.";
    public const string FlagUnread = "A bit of the flags that FFmpeg's decoder does not read.";
    public const string FlagDenoiseStrength = "The strength of the decoder's denoise filter, 0 to 11. FFmpeg's decoder refuses 12 to 15.";
    public const string FlagDenoiseTilt = "Whether the decoder's denoise filter corrects the tilt of the spectrum. It shapes the audio only.";
    public const string FlagDcLevel = "The DC level that the decoder's denoise filter uses. It shapes the audio only.";
    public const string FlagLspCount = "How many line spectral pairs describe each frame: 16 where the bit is set, else 10. It sets the width of the LSP fields.";
    public const string FlagLspQuantizerMode = "Which set of quantizer tables the LSP values use. It does not change any width.";
    public const string FlagLspDefaultMode = "Which set of default LSP values the decoder starts from. It does not change any width.";
    public const string FlagOthers = "Bits 15 to 31 of the flags, which FFmpeg's decoder does not read.";
    public const string FrameTypeTable = "The table that maps the codes a frame sends to the 17 frame types: a group of 3 bits for each type, from which each type's code follows.";
    public const string FrameTypeEntry = "The group of codes of this frame type, shown as the length in bits of its code, or as no code where no code reaches the type.";
    public const string Unused = "Bits of the table after the 51 it uses, which FFmpeg's decoder does not read.";
    public const string ExtraData = "Bytes after the 46 that FFmpeg's decoder takes, which it refuses.";
}

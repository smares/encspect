namespace Encspect.Formats.DtsHd;

internal static class Topics
{
    public const string File = "dtshd-file";
    public const string ChannelArrangements = "dts-channel-arrangements";
    public const string BitrateModes = "bitrate-modes";
}

// What each node of a DTS-HD file is, citing ATSC A/103:2014 Annex E (the DTS-HD file structure) by its clauses and tables, and
// FFmpeg's libavformat/dtshddec.c and MediaInfo's File_Dts.cpp where they read a field.
internal static class Explain
{
    public const string ChunkSize = "The size of the chunk in bytes, a 64-bit big endian number, without the 8-byte ID and the 8-byte size field itself (A/103 E.2.1).";
    public const string Reserved = "Bytes the chunk's size covers beyond the fields that are read: reserved for later versions of the format, which a reader skips (A/103 E.2.4 to E.2.16).";
    public const string Padding = "Zero bytes, one to three, that bring the next chunk to a 32-bit boundary (A/103 E.2.1).";
    public const string TrailingData = "Bytes after the last chunk that do not make a chunk header.";
    public const string UnreadData = "Bytes that are not read: they do not start a chunk, or they lie beyond the limit of chunks that are read.";

    public const string Header = "DTSHDHDR: the first chunk of a .dtshd file, with the version of the file structure, a time code and what the stream holds (A/103 E.2.4, Table E.2).";
    public const string HeaderVersion = "Hdr_Version: the version of the DTS-HD file structure, a 32-bit number (A/103 Table E.2).";
    public const string TimeCode = "Time_Code: a 40-bit field of a reference clock code, six reserved bits and the sample count since midnight at that clock (A/103 Table E.3).";
    public const string RefClock = "RefClockCode: the clock the time stamp counts, 32,000, 44,100 or 48,000 Hz (A/103 Table E.4).";
    public const string TimeStamp = "TimeStamp: the sample count since midnight at the reference clock, which is the time code of the start of the first sample obtained by decoding the third frame of the file (A/103 E.2.4).";
    public const string TimeCodeFrameRate = "TC_Frame_Rate: the frame rate of the time code, in the low four bits (A/103 Table E.5).";
    public const string StreamMetadata = "Bitw_Stream_Metadata: five flags that describe the stream: variable bit rate, peak bit rate smoothing, a navigation table, a core substream and extension substreams (A/103 Table E.6).";
    public const string IsVbr = "Whether the stream has a variable bit rate (0x0001); a constant bit rate otherwise (A/103 Table E.6).";
    public const string HasSmoothing = "Whether peak bit rate smoothing has been performed (0x0002), which spreads the peaks of the extension substreams over the frames before them (A/103 Table E.6).";
    public const string HasNavigation = "Whether the file has a navigation table (0x0004) (A/103 Table E.6).";
    public const string HasCore = "Whether the stream has a core substream (0x0008) (A/103 Table E.6).";
    public const string HasExtension = "Whether the stream has extension substreams (0x0010) (A/103 Table E.6).";
    public const string ReservedFlags = "The flags 0x0020 to 0x8000, which the standard reserves.";
    public const string AudioPresentations = "Num_Audio_Presentations: the number of audio presentations in the stream (A/103 Table E.2).";
    public const string ExtensionSubstreams = "Number_Of_Ext_Sub_Streams + 1 where the extension substream flag is set, else 0: the number of extension substreams, up to four (A/103 E.2.4).";

    public const string FileInfo = "FILEINFO: abbreviated text about the file, such as whether it is variable or constant bit rate and which audio it holds (A/103 E.2.5, Table E.7).";
    public const string FileInfoText = "A null-terminated ASCII string describing the file (A/103 Table E.7). FFmpeg takes the whole chunk as the string; MediaInfo reads a length byte first.";

    public const string CoreMetadata = "CORESSMD: metadata for the core substream (A/103 E.2.6, Table E.8).";
    public const string CoreRate = "Core_Ss_Max_Sample_Rate_Hz: the highest sampling frequency of the core substream, a 24-bit number (A/103 Table E.8).";
    public const string CoreBitrate = "Core_Ss_Bit_Rate_Kbps: the bit rate of the core substream in kilobits a second (A/103 Table E.8).";
    public const string CoreMask = "Core_Ss_Channel_Mask: the loudspeaker positions of the channels in the core substream, in the bits of Table E.9 (A/103 E.2.6).";
    public const string CorePayload = "Core_Ss_Frame_Payload_In_Bytes: the size of a core substream frame payload in bytes (A/103 Table E.8).";

    public const string ExtensionMetadata = "EXTSS_MD: metadata for all extension substreams together (A/103 E.2.7, Table E.10).";
    public const string ExtensionAverage = "Ext_Ss_Avg_Bit_Rate_Kbps: the average bit rate of all extension substreams together, in kilobits a second (A/103 Table E.10).";
    public const string ExtensionPeak = "Ext_Ss_Peak_Bit_Rate_Kbps: the peak bit rate of all extension substreams together, in kilobits a second; present when the stream has a variable bit rate (A/103 Table E.10).";
    public const string ExtensionBuffer = "Pbr_Smooth_Buff_Size_Kb: the size of the peak bit rate smoothing buffer in kilobits; present when the stream has a variable bit rate (A/103 Table E.10).";
    public const string ExtensionPayload = "Ext_Ss_Frame_Payload_In_Bytes: the frame payload of all extension substreams added together; present when the stream has a constant bit rate (A/103 Table E.10).";

    public const string PresentationHeader = "AUPR-HDR: metadata for one audio presentation of the stream, with its sampling frequency, length and channels (A/103 E.2.8, Table E.11). FFmpeg takes the sampling frequency, the number of channels and the length from it.";
    public const string PresentationIndex = "Audio_Pres_Index: which audio presentation the chunk describes (A/103 Table E.11).";
    public const string PresentationMetadata = "Bitw_Aupres_Metadata: flags for a backward compatible core component (present, and whether in the core or an extension substream), a lossless component and an LBR component (A/103 Table E.12).";
    public const string HasBcCore = "Whether the presentation has a backward compatible core component (0x0001) (A/103 Table E.12).";
    public const string BcCoreLocation = "Where the backward compatible core component is: in an extension substream when set (0x0002), in the core substream otherwise; without meaning when there is no such component (A/103 Table E.12).";
    public const string HasLossless = "Whether the presentation has a lossless component (0x0004) (A/103 Table E.12).";
    public const string HasLbr = "Whether the presentation has a low bit rate (LBR) component (0x0008) (A/103 Table E.12).";
    public const string PresentationRate = "Max_Sample_Rate_Hz: the highest sampling frequency of the presentation, a 24-bit number (A/103 Table E.11).";
    public const string FrameCount = "Num_Frames_Total: the number of frames in the presentation (A/103 Table E.11).";
    public const string FrameSamples = "Samples_Per_Frame_At_Max_Fs: the samples one frame holds at the highest sampling frequency (A/103 Table E.11).";
    public const string OriginalSamples = "Num_Samples_Orig_Audio_At_Max_Fs: the number of samples of the original audio at the highest sampling frequency, a 40-bit number (A/103 Table E.11).";
    public const string PresentationMask = "Channel_Mask: the loudspeaker positions of the presentation's channels, in the bits of Table E.9 (A/103 E.2.8).";
    public const string CodecDelay = "Codec_Delay_At_Max_Fs: the delay of the codec in samples at the highest sampling frequency, which an authoring tool turns into the number of frames to skip (A/103 E.2.8).";
    public const string BcCoreRate = "BC_Core_Max_Sample_Rate_Hz: the highest sampling frequency of the backward compatible core component; present when the presentation has one in an extension substream (A/103 Table E.11).";
    public const string BcCoreBitrate = "BC_Core_Bit_Rate_Kbps: the bit rate of the backward compatible core component in kilobits a second (A/103 Table E.11).";
    public const string BcCoreMask = "BC_Core_Channel_Mask: the loudspeaker positions of the backward compatible core component, in the bits of Table E.9 (A/103 Table E.11).";
    public const string LsbTrim = "LSB_Trim_Percent: present when the presentation has a lossless component (A/103 Table E.11). The standard does not say what it measures.";
    public const string Speakers = "The speakers the bits of the mask name (A/103 Table E.9), in the order of the bits.";

    public const string PresentationInfo = "AUPRINFO: text about one audio presentation (A/103 E.2.9, Table E.13).";
    public const string PresentationInfoIndex = "Audio_Pres_Info_Text_Index: which audio presentation the text describes (A/103 Table E.13).";
    public const string PresentationInfoText = "A null-terminated ASCII string about the presentation (A/103 Table E.13).";

    public const string NavigationTable = "NAVI-TBL: navigation pointers for a variable bit rate stream: the offsets of random access points (A/103 E.2.10, Table E.14).";
    public const string NavigationEntries = "Num_Entries_In_Navi_Tbl: the number of entries in the table (A/103 Table E.14).";
    public const string NavigationInterval = "Navi_Interval_In_Frames: the distance between two entries in frames (A/103 Table E.14).";
    public const string NavigationEntrySize = "Navi_Tbl_Entry_Byte_Size: the size of an entry in bytes (A/103 Table E.14).";
    public const string NavigationData = "The entries, each the offset in bytes to a random access point (A/103 Table E.14). Not read.";

    public const string BitShaving = "BITSHVTB: instructions to a post-processing tool about the number of bits to remove from the least significant part of the lossless stream at particular frames (A/103 E.2.11, Table E.15).";
    public const string BitShavingIndex = "Bit_Shave_Audio_Pres_Index: the audio presentation the table is for (A/103 Table E.15).";
    public const string BitShavingEntries = "Num_Entries_In_Bitshave_Tbl: the number of pairs of a frame index and a number of bits to shave (A/103 Table E.15).";
    public const string BitShavingData = "The pairs, each a 4-byte frame index and a 1-byte number of bits (A/103 Table E.15). Not read.";

    public const string StreamData = "STRMDATA: the DTS-HD stream itself, core frames and extension substreams, which is all of the file that is audio (A/103 E.2.12, Table E.16). The DTS parser reads it as it reads a .dts file.";
    public const string Timecode = "TIMECODE: time code metadata: the clock, the frame rate and the positions of the start and a reference point, in samples since midnight (A/103 E.2.13, Table E.17).";
    public const string TimecodeClock = "Timecode Clock: the sampling frequency in which the samples since midnight are counted, a 32-bit number (A/103 Table E.17).";
    public const string TimecodeFrameRate = "Timecode Frame Rate: the frame rate of the time code, coded as TC_Frame_Rate (A/103 Table E.5, E.2.13).";
    public const string TimecodeStart = "Start samples since midnight: the sample count at the start of the stream (A/103 Table E.17).";
    public const string TimecodeStartResidual = "Start Residual: a residual of the start position (A/103 Table E.17), which the standard does not describe further.";
    public const string TimecodeReference = "Reference samples since midnight: the sample count at a reference point (A/103 Table E.17).";
    public const string TimecodeReferenceResidual = "Reference Residual: a residual of the reference position (A/103 Table E.17), which the standard does not describe further.";

    public const string BuildVersion = "BUILDVER: a null-terminated string that the standard lists as the DTS-HD encoder version identification and describes as compiler information (A/103 E.2.14); it does not say whether it names the encoder or the tool that wrote the file.";
    public const string BuildVersionText = "The build text, null terminated ASCII (A/103 Table E.18).";
    public const string Blackout = "BLACKOUT: silent audio frames of the stream's format (A/103 E.2.15, Table E.19). Not read.";
    public const string BlackoutData = "The encoded silence frame data (A/103 Table E.19). Not read.";
    public const string BranchPoints = "BRANCHPT: the markers of branch point entries: the frame index of each point where playback can branch (A/103 E.2.16, Table E.20).";
    public const string BranchEntries = "Num_Entries_In_Branch_Point_Tbl: the number of entries in the table (A/103 Table E.20).";
    public const string BranchEntrySize = "Branch_Point_Tbl_Entry_Byte_Size: the size of an entry in bytes (A/103 Table E.20).";
    public const string BranchData = "The entries, each the frame index of a branch point (A/103 Table E.20). Not read.";
    public const string UnknownChunk = "A chunk this analyzer does not know. A reader skips chunks it does not understand (A/103 E.2).";
    public const string UnknownData = "The payload of a chunk that is not defined. Not read.";
    public const string ExtensionMetadataBeforeHeader = "EXTSS_MD fields that depend on the variable bit rate flag of DTSHDHDR, which comes later in this file, so they are not read (A/103 Table E.10).";
}

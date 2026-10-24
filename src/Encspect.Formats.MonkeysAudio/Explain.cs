namespace Encspect.Formats.MonkeysAudio;

// The background articles the Monkey's Audio nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string File = "monkeys-audio-file";
    public const string SeekTable = "monkeys-audio-seek-table";
    public const string Frames = "monkeys-audio-frames";
    public const string RangeCoding = "monkeys-audio-range-coding";
    public const string Prediction = "monkeys-audio-prediction";
    public const string DescriptorMd5 = "monkeys-audio-descriptor-md5";
    public const string MidSideStereo = "mid-side-stereo";
    public const string Crc = "crc";
    public const string Verification = "verification";
    public const string Channels = "channel-layouts";
    public const string BitrateModes = "bitrate-modes";
}

// What each Monkey's Audio node is, in the words the report shows next to it. Constants only: the frames of a long file are built by the hundred
// thousand, so a description picks one of these and never allocates. The source is the Monkey's Audio SDK 13.42 (MACLib.h, APEHeader.cpp,
// APEInfo.cpp, APEDecompress.cpp, APEDecompressCore.cpp, APECompressCreate.cpp, BitArray.cpp, UnBitArray.cpp, NewPredictor.cpp, Prepare.h), and the
// files its mac tool wrote.
internal static class Explain
{
    public const string Stream = "A Monkey's Audio file: a descriptor and a header, a seek table, the header of the WAV file that was compressed, the compressed frames, and the bytes that followed the audio in the WAV file (Monkey's Audio SDK 13.42, MACLib.h, \"APE File Format Overview\").";
    public const string Junk = "Bytes before the descriptor. The SDK scans up to 1 MiB for it, to get past an ID3v2 tag (APEHeader.cpp, FindDescriptor).";

    public const string Descriptor = "The 52-byte descriptor that files of version 3.98 and later start with: the sizes of everything that follows it, and the MD5 of the coded data (MACLib.h, APE_DESCRIPTOR).";
    public const string Signature = "\"MAC \" for integer audio and \"MACF\" for floating-point audio, the four characters every Monkey's Audio file starts with (APEHeader.cpp).";
    public const string FileVersion = "The version of the file format the encoder wrote, stored times 1000: 3.99 is 3990. It decides how the frames are coded, so a decoder must follow it.";
    public const string ProgramVersion = "The version of the program that wrote the file, stored times 1000 (13.42 is 13420). Monkey's Audio has stored it since 13.17; earlier files store 0.";
    public const string DescriptorBytes = "The size of the descriptor, 52. A larger value leaves room for fields added later: the SDK reads the header after that many bytes (APEHeader.cpp, AnalyzeCurrent).";
    public const string HeaderBytes = "The size of the header that follows the descriptor, 24.";
    public const string SeekTableBytes = "The size of the seek table in bytes, 4 for each frame.";
    public const string HeaderDataBytes = "The size of the WAV header data that follows the seek table: the header of the file that was compressed, kept so that a decoder can give it back.";
    public const string FrameDataBytes = "The size of all frames together in bytes, in two 32-bit fields, the low half first, so that a file can hold more than 4 GiB of them.";
    public const string TerminatingBytes = "The size of the WAV terminating data that follows the frames: the bytes after the audio in the compressed file, such as chunks after its data chunk.";
    public const string DescriptorMd5 = "The MD5 of the coded data: the WAV header data, the frames and the terminating data, then the header and the seek table (MACLib.cpp, VerifyFileW2). The descriptor itself is not covered. The analyzer compares it in every analysis.";
    public const string ComputedMd5 = "The MD5 the analyzer computed over the same bytes.";
    public const string IsMd5Valid = "Whether the stored MD5 equals the computed one. A difference says some of the coded bytes changed, not which; the frame CRCs, compared when the audio is verified, can locate it.";

    public const string Header = "The 24-byte header after the descriptor: the compression level, the format flags, the frame sizes and the audio format (MACLib.h, APE_HEADER).";
    public const string OldHeader = "The header that files before version 3.98 start with, with the optional fields that follow it: they have no descriptor (APEHeader.h, APE_HEADER_OLD).";
    public const string CompressionLevel = "How hard the encoder worked: 1000 fast, 2000 normal, 3000 high, 4000 extra high, 5000 insane. The higher levels run longer neural network filters, and extra high and insane use longer frames (NewPredictor.cpp, APECompressCreate.cpp).";
    public const string FormatFlags = "Flags for the format of the compressed file: bit 0 8-bit, 1 frame CRCs, 2 peak level stored, 3 24-bit, 4 seek element count stored, 5 WAV header created on decompression, 6 to 8 and 10 AIFF, W64, SND and CAF sources, 9 big-endian, 11 signed 8-bit, 12 floating point (MACLib.h).";
    public const string FormatFlagNames = "The flags that are set, by name.";
    public const string BlocksPerFrame = "The number of blocks, samples of every channel at one instant, in each frame but the last. The encoder uses 73,728, four times as many at extra high and sixteen times as many at insane (APECompressCreate.cpp).";
    public const string FinalFrameBlocks = "The number of blocks in the last frame, which holds what is left after the full frames.";
    public const string TotalFrames = "The number of frames in the file.";
    public const string BitsPerSample = "The bits of each sample of the compressed audio: 8, 16, 24 or 32.";
    public const string Channels = "The number of channels, 1 to 32. The encoder codes a stereo pair as the mid and the side channel.";
    public const string SampleRate = "The samples of each channel per second.";
    public const string PeakLevel = "The highest sample value, which files before version 3.98 may store when their flags say so; the format marks it obsolete (MACLib.h).";
    public const string SeekElements = "How many entries the seek table holds, stored only where the format flags say so; without it the table has one entry for each frame.";

    public const string SeekTable = "The offset of the first byte of each frame, 4 bytes for each frame, counted from the start of the descriptor (MACLib.h, APEHeader.cpp).";
    public const string SeekEntry = "Where one frame starts, as an unsigned 32-bit offset. The SDK adds 2^32 whenever an offset is lower than the one before it, so that files over 4 GiB work (APEHeader.cpp, Convert32BitSeekTable).";
    public const string HeaderData = "The header of the WAV file that was compressed, which a decoder gives back before the samples. Files of other kinds (AIFF, CAF) keep their own header here.";
    public const string TerminatingData = "The bytes that followed the audio in the compressed file, kept so that a decoder can give them back after the samples.";
    public const string TrailingData = "Bytes after the terminating data that no tag claims. The descriptor does not count them.";

    public const string Frame = "One frame: its stored CRC, special frame codes where it has them, and its range-coded samples. Frames follow one another byte by byte in 32-bit words whose bytes are stored in reverse order, so a frame's node covers the words its bytes lie in, and the word at either end may be shared with the frame next to it (APECompressCreate.cpp, FixupFrame).";
    public const string FrameCrc = "The CRC-32 of the frame's decoded audio in WAV layout, inverted and shifted right by one so that it takes 31 bits; bit 31 of the word flags special frame codes after it (APEDecompressCore.cpp, StartFrame and EndFrame). It covers decoded audio, so the analyzer compares it only when the stream is decoded.";
    public const string SpecialCodes = "Flags for a frame the encoder stores without coding every sample: 1 the left channel is silent (the only channel of a mono file, where no other code counts), 2 the right channel is silent, 4 the right channel repeats the left (Prepare.h). Files of more than two channels ignore them.";
    public const string RangeCodedData = "The frame's samples: a dummy byte, then what the predictors left of each sample, written with a range coder (UnBitArray.cpp, BitArray.cpp).";
    public const string CodedData = "The frame's coded samples, in the coding of the file's version.";
    public const string Unlisted = "Frames after the first million, which the report does not list one by one. They are read and checked in every case.";

    public const string Statistics = "Figures computed over the frames of the stream.";
    public const string StatFrames = "The number of frames the seek table locates.";
    public const string StatSamples = "The samples of each channel, from the frame sizes the header gives.";
    public const string StatDuration = "The duration of the samples at the sample rate.";
    public const string StatRate = "The sample rate of the audio, from the header.";
    public const string StatChannels = "The number of channels, from the header.";
    public const string StatLayout = "The speakers the channels feed. The format does not state positions; one channel is taken as the center and two as left and right.";
    public const string StatBits = "The bits of each sample, from the header.";
    public const string StatLevel = "The compression level, from the header.";
    public const string StatSilentFrames = "The frames flagged as silent in every channel they code (both channels of a stereo file, the only channel of a mono file), which hold no coded samples.";
    public const string StatPseudoStereoFrames = "The frames flagged as pseudo-stereo, which code one channel and repeat it in the other.";
    public const string StatStoredChecksums = "The frames of a file without frame CRCs, each of which starts with a checksum of 30 bits that the analyzer does not read or compare (Old/UnMACOld.cpp, DecompressFrame).";
    public const string StatStoredCrcs = "The frames that store a CRC of their decoded audio. The analyzer compares them only when the stream is decoded.";
    public const string StatMd5 = "Whether the MD5 of the coded data that the descriptor stores equals the one computed from the file.";
    public const string AverageBitrate = "The bits of the frames over the time their samples last.";
    public const string MinimumBitrate = "The bitrate of the frame with the lowest bitrate.";
    public const string MaximumBitrate = "The bitrate of the frame with the highest bitrate.";
    public const string Smallest = "The smallest frame.";
    public const string Largest = "The largest frame.";
    public const string BitrateMode = "Whether the frames all have the same bitrate: variable where they differ, constant where they do not, and unknown for a single frame, since one frame shows no variation. Lossless frames are as long as their audio needs.";
    public const string Distribution = "How many frames fall in each bitrate range of 32 kbps.";
    public const string DistributionBin = "The frames whose bitrate falls in this range, and their share.";
    public const string BitrateSeries = "The bitrate of each frame: its bits over the time its samples last.";
    public const string BitrateOverTimeSeries = "The bitrate of each second of audio: the bits of the frames that start in that second over the time their samples last.";

    public const string Encoder = "The program that wrote the file, as the descriptor and the format version show it, with the evidence and the confidence in it.";
    public const string ConfidenceHigh = "High: the descriptor states the version of the program that wrote the file.";
    public const string ConfidenceMedium = "Medium: only the version of the file format is known, which Monkey's Audio writes and the format's other tools copy.";
    public const string Evidence = "Something in the file that points to the encoder.";
    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies inside the Monkey's Audio data, which the tag breaks; the analyzer does not read them as frames.";
}

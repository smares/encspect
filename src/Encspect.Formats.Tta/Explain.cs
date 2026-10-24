namespace Encspect.Formats.Tta;

// What each TTA node is, in the words the report shows next to it. Constants only: the frames of a long file are built by the hundred
// thousand, so a description picks one of these and never allocates. The sources are the TTA format description on the TTA authors' site
// (tta.sourceforge.net, "Format Description"), Matroska's codec mapping for A_TTA1 and the files FFmpeg writes and reads.
internal static class Explain
{
    public const string Stream = "The TTA stream: the header, the seek table and then the frames of audio, one after another (TTA format description).";
    public const string Header = "The TTA1 header: the signature, the audio format, the number of channels, the bits per sample, the sample rate, the number of samples and a CRC-32 over these fields (TTA format description).";
    public const string Signature = "The characters \"TTA1\" that begin the stream; the last one is the version of the format (TTA format description).";
    public const string SignatureV2 = "The characters \"TTA2\" that begin a stream of the second version, which the format description lists as in development; the analyzer does not read it further.";
    public const string AudioFormatSimple = "The audio format is 1, the value FFmpeg 6.1.1 and libtta-c 2.3 write in Encspect's test files; the format description does not say what the values mean.";
    public const string AudioFormatEncrypted = "The audio format is 2: libtta-c 2.3 writes it in Encspect's test file of a stream encoded with a password, and FFmpeg 6.1.1 asks for a password to open such a file. The format description does not define the value.";
    public const string AudioFormatOther = "The audio format is neither 1 nor 2, the values in Encspect's test files (written by FFmpeg 6.1.1 and libtta-c 2.3); the format description does not define it.";
    public const string Channels = "The number of channels (TTA format description).";
    public const string BitsPerSample = "The number of bits of each sample; the compression theory page of the TTA authors names 8, 16 and 24.";
    public const string SampleRate = "The sample rate in Hz (TTA format description).";
    public const string HeaderSamples = "The number of samples in each channel of the stream (TTA format description, \"data length in samples\"); the seek table has one entry for each frame these samples fill.";
    public const string HeaderCrc = "The CRC-32 the header stores, over the 18 bytes before it. The analyzer compares it in every analysis.";
    public const string HeaderComputedCrc = "The CRC-32 the analyzer computed over the 18 bytes of the header before the stored one.";
    public const string HeaderIsCrcValid = "Whether the stored CRC-32 of the header equals the one computed.";

    public const string SeekTable = "The seek table: one 4-byte entry for each frame, then a CRC-32 over the entries (TTA format description). A player adds the lengths to find a frame.";
    public const string SeekPoints = "The entries of the seek table, in the order of the frames; the number of entries is the number of samples over the samples in a frame, rounded up.";
    public const string SeekPoint = "One entry of the seek table: the length in bytes of its frame (TTA format description, \"the current frame data length\"). In Encspect's test files, written by FFmpeg 6.1.1 and libtta-c 2.3, it includes the frame's CRC-32.";
    public const string TableCrc = "The CRC-32 the seek table stores, over its entries. The analyzer compares it in every analysis.";
    public const string TableComputedCrc = "The CRC-32 the analyzer computed over the entries of the seek table.";
    public const string TableIsCrcValid = "Whether the stored CRC-32 of the seek table equals the one computed.";

    public const string ContainedFrames = "The TTA frames the container holds, one to a packet.";
    public const string Frame = "One frame: the coded samples of every channel, about a second of audio, then a CRC-32 over the coded bytes before it; a decoder can start at any frame (TTA format description).";
    public const string FrameData = "The coded samples of the frame, with no header of their own: the values of every channel in turn, sample by sample.";
    public const string CutFrameData = "The bytes of the frame that the data holds; the data ends before the frame does.";
    public const string SmallFrameData = "The bytes of a frame that its seek table entry states shorter than the CRC-32 every frame ends with.";
    public const string FrameCrc = "The CRC-32 the frame stores, over the coded bytes before it. The analyzer compares it in every analysis.";
    public const string FrameComputedCrc = "The CRC-32 the analyzer computed over the coded bytes of the frame.";
    public const string FrameIsCrcValid = "Whether the stored CRC-32 of the frame equals the one computed.";

    public const string PacketTrailing = "Bytes of the stream's data that follow the end of the frames the seek table lists and are no tag; the analyzer does not read them.";
    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies between the TTA frames, which the tag breaks; the analyzer does not read them as frames.";
    public const string UnreadData = "The bytes after the signature of a stream whose header the analyzer does not read.";
    public const string ConfigurationExtra = "Bytes of the configuration the container stores for the track that follow its 22-byte TTA1 header; the analyzer does not read them.";
    public const string ConfigurationHeader = "The TTA1 header that the container stores for the track (Matroska's CodecPrivate); the frames follow without a header or a seek table.";

    public const string Statistics = "Figures computed over the frames of the stream.";
    public const string StatFrames = "The number of frames in the stream, a frame the data cuts short not counted.";
    public const string StatSamples = "The samples of each channel the frames hold, added up; a frame the data cuts short is not counted.";
    public const string StatDuration = "The duration of the samples at the sample rate.";
    public const string StatRate = "The sample rate of the audio, from the header.";
    public const string StatChannels = "The number of channels, from the header.";
    public const string StatBits = "The bits of each sample, from the header.";
    public const string StatFrameLength = "The samples in every frame but the last: the sample rate times 256/245, about 1.045 seconds of audio (TTA format description, \"frame time\").";
    public const string StatFrameChecksums = "The frames whose CRC-32 equals the one computed over the frame.";
    public const string StatFrameChecksumsFailed = "The frames whose CRC-32 differs from the one computed.";

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
}

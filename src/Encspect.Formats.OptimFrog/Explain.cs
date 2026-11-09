namespace Encspect.Formats.OptimFrog;

// What each OptimFROG node is, in the words the report shows next to it. Constants only. The source is the author's specification of the compressed
// file format (OptimFROG 5.100 SDK, SDK/Documentation/format.txt), its changelog, and the files its encoders wrote in Encspect's tests; where a
// sentence states what those files show, it says so.
internal static class Explain
{
    public const string Stream = "An OptimFROG file: a main block, the stored header of the audio file that was compressed, the data blocks with the compressed audio, the stored footer and optional blocks, each with a 4-byte ID and a length (format.txt).";

    public const string MainBlock = "The main block: the number of samples, the sample type, the channel configuration, the sample rate, the encoder ID and the compression of the whole file (format.txt, section 1).";
    public const string HeadBlock = "The header block: all the data from the beginning of the audio file that was compressed until the start of its sample data (format.txt, section 2).";
    public const string CompBlock = "A data block: compressed sample data with all the information needed to decode it independently, and a CRC-32 over it (format.txt, section 3).";
    public const string TailBlock = "The footer block: all the data from the end of the sample data to the end of the audio file that was compressed (format.txt, section 4).";
    public const string RecoveryBlock = "The recovery block: recovery information used to repair header errors and sector-level errors (format.txt, section 5). The analyzer does not interpret it.";
    public const string Md5Block = "The MD5 block: the MD5 digest of the original raw PCM input data (format.txt, section 9).";
    public const string UnknownBlock = "A block whose ID the specification does not define. The walk follows it by its length, since a block's data is skipped when it is not known.";

    public const string BlockId = "The 4 characters that name the kind of block (format.txt).";
    public const string BlockLength = "The number of bytes of data after the block's ID and length (format.txt).";

    public const string HeaderData = "The first bytes of the audio file that was compressed, up to its sample data, kept so that decoding can give the file back (format.txt, section 2). The analyzer reads the chunks of a RIFF header, the format of a WAVE header and lossyWAV's fact chunk.";
    public const string TerminatingData = "The last bytes of the audio file that was compressed, after its sample data, such as chunks after the data chunk (format.txt, section 4).";

    public const string MainData = "The bytes of a main block that holds fewer than the 15 bytes of its fields.";
    public const string TotalSamples = "The number of samples in the audio file, over all channels: format.txt counts a single sample value, not a sample point, so the samples of each channel are this number divided by the channels.";
    public const string SampleType = "The type of the container each sample is stored in: unsigned or signed integers of 8, 16, 24 or 32 bits, or 32-bit floating point (format.txt, appendix A). Values above 10 are not defined.";
    public const string ChannelConfiguration = "The number of channels and, as far as the encoder uses it, their mapping: MONO or STEREO_LR, left and right (format.txt, appendix B).";
    public const string SampleRate = "The sample rate in Hz (format.txt).";
    public const string EncoderId = "The encoder ID: the version of the encoder and the system it was built for in one number, version = (ID >> 4) + 4500 and build system = ID & 15 (format.txt, appendix C).";
    public const string EncoderVersion = "The version of the encoder that wrote the file, written x.xxx.";
    public const string BuildSystem = "The build of the encoder executable, which format.txt says is kept to track system-dependent problems: the operating system and processor, or unknown (appendix C).";
    public const string Compression = "The compression the encoder was run with: the mode in the upper five bits and the speedup in the lower three (format.txt, appendix D).";
    public const string Mode = "The compression mode (format.txt, appendix D). The changelog of 4.506 says the encoder writes the exact mode for information only; the article lists which options of 5.100 store the same mode.";
    public const string Speedup = "The speedup parameter, 1x, 2x or 4x (format.txt, appendix D), deprecated in 4.506. 5.100 stores 4x in every file Encspect's tests hold.";
    public const string MinDecoderVersion = "The minimum decoder version required to decode the file, added in 5.100 (format.txt, section 1); the field stores it less 4500.";
    public const string AdditionalFields = "Bytes of the main block after the fields this version of the specification defines; format.txt asks a reader to skip fields it does not know.";

    public const string CompFieldsCut = "The bytes of a data block that the data holds; they end before the block's 14 bytes of fields do.";
    public const string BlockCrc = "The CRC-32 the block stores, over its data after this field. The analyzer compares it in every analysis; the files of OptimFROG 4.509 and 5.100 in Encspect's tests use zlib's CRC-32.";
    public const string BlockComputedCrc = "The CRC-32 the analyzer computed over the block's data after the stored CRC-32.";
    public const string BlockIsCrcValid = "Whether the stored CRC-32 of the block equals the one computed.";
    public const string BlockSamples = "The number of samples in the block over all channels, as in the main block (format.txt, section 3).";
    public const string BlockSampleType = "The sample type of the block, which the encoder writes into every block so that each can be decoded alone (format.txt, section 3).";
    public const string BlockChannelConfiguration = "The channel configuration of the block (format.txt, section 3).";
    public const string BlockReserved = "Two bytes the specification calls reserved for internal use. The analyzer shows them and does not judge them.";
    public const string BlockEncoderId = "The encoder ID of the block (format.txt, section 3).";
    public const string CompressedData = "The compressed sample data of the block. Only OptimFROG's own library decodes it; the analyzer does not read it.";

    public const string Md5Digest = "The digest as 32 hexadecimal digits, over the raw PCM data of the source. The analyzer does not compare it, which needs a decoder. OptimFROG 5.001 and earlier computed it other than RFC 1321 says for audio of 56 to 63 bytes modulo 64 (format.txt).";
    public const string Md5Data = "The bytes of an MD5 block that does not hold the 16 bytes of a digest.";
    public const string RecoveryData = "Recovery data, which the analyzer does not interpret.";
    public const string UnknownData = "The data of a block with an ID the specification does not define.";

    public const string FactChunk = "The fact chunk of the stored WAV header. lossyWAV writes its version, the time of the run and its settings in it as text.";
    public const string ChunkList = "The RIFF chunks the stored data holds, listed by their IDs and sizes.";
    public const string Chunk = "One RIFF chunk: its ID, its size and its data.";
    public const string ChunkId = "The 4 characters that name the chunk.";
    public const string ChunkSize = "The size of the chunk's data in bytes.";
    public const string FormatTag = "The format tag of the fmt chunk in the stored WAV header, wFormatTag: 1 is PCM, 3 IEEE floating point, 0xFFFE the extensible format (mmreg.h). The analyzer reads the chunk only for its channels and channel mask.";
    public const string WavChannels = "The number of channels in the fmt chunk of the stored WAV header.";
    public const string ChannelMask = "Which speaker positions the channels of a WAVE_FORMAT_EXTENSIBLE header feed, one bit for each position.";
    public const string Speakers = "The speakers the channel mask names.";

    public const string Encoder = "The program that wrote the file, as the encoder ID in the main block shows it, with the evidence and the confidence in it.";
    public const string ConfidenceHigh = "High: the main block states the version of the encoder that wrote the file.";
    public const string Evidence = "Something in the file that points to the encoder.";

    public const string Trailing = "Bytes after the last block that belong to no tag; the analyzer does not read them.";

    public const string Statistics = "Figures computed over the blocks of the file.";
    public const string StatBlocks = "The number of data blocks in the file, a block the data cuts short not counted.";
    public const string StatSamples = "The samples of each channel the main block states: its total over all channels divided by the channels.";
    public const string StatDuration = "The duration of the samples at the sample rate.";
    public const string StatRate = "The sample rate of the audio, from the main block.";
    public const string StatChannels = "The number of channels, from the main block's channel configuration.";
    public const string StatBits = "The bits of the container of each sample, from the sample type; the floating-point types are 32-bit.";
    public const string StatLayout = "The speakers the channels feed: the channel mask of the stored WAV header where it has one, else the channel configuration, with one channel as center and two as left and right.";
    public const string StatMode = "The compression mode stored in the main block (format.txt, appendix D). It is informational: see the article for the options that store the same mode.";
    public const string StatSpeedup = "The speedup stored in the main block (format.txt, appendix D).";
    public const string StatSampleType = "The sample type stored in the main block (format.txt, appendix A).";
    public const string StatBuildSystem = "The build system of the encoder, from its encoder ID (format.txt, appendix C).";
    public const string StatBlockChecksums = "The data blocks whose CRC-32 equals the one computed over the block.";
    public const string StatBlockChecksumsFailed = "The data blocks whose CRC-32 differs from the one computed.";

    public const string AverageBitrate = "The bits of the data blocks, their headers included, over the time their samples last.";
    public const string MinimumBitrate = "The bitrate of the block with the lowest bitrate.";
    public const string MaximumBitrate = "The bitrate of the block with the highest bitrate.";
    public const string Smallest = "The smallest block.";
    public const string Largest = "The largest block.";
    public const string BitrateMode = "Whether the blocks all have the same bitrate: variable where they differ, constant where they do not, and unknown for a single block, since one block shows no variation. Lossless blocks are as long as their audio needs.";
    public const string Distribution = "How many blocks fall in each bitrate range of 32 kbps.";
    public const string DistributionBin = "The blocks whose bitrate falls in this range, and their share.";
    public const string BitrateSeries = "The bitrate of each block: its bits over the time its samples last.";
    public const string BitrateOverTimeSeries = "The bitrate of each second of audio: the bits of the blocks that start in that second over the time their samples last.";
}

namespace Encspect.Formats.WavPack;

// What each node of a WavPack file of format version 1 to 3 is, in the words the report shows next to it. The sources: libwavpack 5.6.0's src/unpack3.h, unpack3_open.c
// and unpack3.c, and the source of WavPack 3.97 (wavpack.c, wavpack.h, wvunpack.c), which the design in docs/design/wavpack-legacy.md follows; no specification of
// these versions exists.
internal static class LegacyExplain
{
    public const string Topic = "wavpack-legacy";

    public const string Stream = "A WavPack file of format version 1, 2 or 3 (WavPack 1.0 to 3.97): the source WAV file's header, a WavPack header and one bitstream, with no blocks (unpack3_open.c).";
    public const string RawStream = "A raw WavPack file of format version 3: the WavPack header is first, with no WAV header before it, because the source was raw audio (3.97 wavpack.c, option -r).";
    public const string StoredHeader = "The source WAV file's header, copied unchanged up to and including the 8-byte header of its data chunk (3.97 wavpack.c); the WavPack header follows at once.";
    public const string Riff = "The RIFF chunk of the source WAV file: the signature, the size the source stated and the form type. The size is the source file's, not this file's.";
    public const string OtherChunk = "A chunk of the source WAV file other than the format and the audio, kept in the stored header.";
    public const string DataChunk = "The header of the source's data chunk. The audio itself is not stored here: the bitstream after the WavPack header codes it.";
    public const string DataChunkSize = "The size of the source's audio in bytes. It is not the size of this file's data: the chunk runs past the end of the file. libwavpack takes the total samples of a file from it (unpack3_open.c).";

    public const string Header = "The WavPack header: 10 bytes in version 1, 12 in version 2 and 36 in version 3 (unpack3.h, WavpackHeader3).";
    public const string HeaderId = "The signature \"wvpk\".";
    public const string HeaderSize = "The size of the header less its first 8 bytes, as the encoders write it: 2, 4 and 28 for versions 1, 2 and 3 in Encspect's test files. No reader uses it.";
    public const string Version = "The format version, 1 to 3. libwavpack reports it as the major version of the program that wrote the file; a reader refuses any other.";
    public const string LossyBitLimit = "The most bits the coder spends on the size of a sample in the older lossy mode, as the decoder reads it; 0 for a lossless file. The encoder's option stores it less 3 (3.97 wavpack.c).";
    public const string HybridTarget = "The bits per sample the hybrid encoder aimed at, summed over the channels and times 256. The encoder derives it from the bitrate or the bits per sample asked for (3.97 wavpack.c).";
    public const string Flags = "The 16 flags of the version 3 header (unpack3.h). Some name a coding mode, some the sample size, and three are never stored.";
    public const string Shift = "The low bits the samples lack: the decoder shifts each decoded sample left by this many bits.";
    public const string TotalSamples = "The samples of each channel the file holds, as the encoder counted them. libwavpack takes the count from the data chunk's size instead; 3.97's decoder takes this one.";
    public const string Crc = "A check of the audio this file decodes to: the sum, over the samples, of the previous value times 3 plus the sample (stereo: times 9 plus 3 times left plus right). Only a decoder can compare it.";
    public const string Crc2 = "The same check over the audio with the correction file added; 0 in files that have none. Only a decoder can compare it.";
    public const string Extension = "The extension of the file the audio came from, such as \"WAV\" or \"RAW\".";
    public const string ExtraCount = "How many bytes follow the last whole sample of a raw source, 0 to 3 (3.97 wavpack.c).";
    public const string ExtraBytes = "The bytes that follow the last whole sample of a raw source, so decoding gives the file back whole.";

    public const string Bitstream = "The coded audio: one bitstream from the end of the header to the trailer, or to the end of the file. Its end is known only to a decoder, which reads until the samples the file states are decoded.";
    public const string Trailer = "What the encoder kept of the source after the audio: 16 bytes of 0xFF, then the source's chunks (3.97 wavpack.c). The 0xFF bytes keep an old decoder from reading the chunks as audio.";
    public const string TrailerMarker = "16 bytes of 0xFF: a run of ones that every word reader of the decoder takes as the end of the coded data.";
    public const string TrailerChunk = "A chunk of the source WAV file that followed its audio.";
    public const string TrailerFurther = "Chunks of the trailer past the ones listed.";

    public const string Statistics = "Figures from the headers of the stream. Without a decoder the audio is not read, so the bitstream's length and its end are what the file shows.";
    public const string StatSamples = "The samples of each channel the file holds: the header's total in version 3, the data chunk's size over the size of one sample of all channels in versions 1 and 2.";
    public const string StatDuration = "The duration of the samples at the sample rate.";
    public const string StatRate = "The sample rate of the audio: the stored WAV header's, 44,100 Hz for a raw file, as 3.97 takes it (wavpack.c).";
    public const string StatChannels = "The number of channels: the stored WAV header's, or the mono flag for a raw file.";
    public const string StatLayout = "The speakers the channels feed: the front center for one channel, the front left and right for two, as libwavpack's channel mask gives them (unpack3_open.c).";
    public const string StatBits = "The bits of each sample: the stored WAV header's; 16 for a raw file, 24 for a raw file of 3-byte samples.";
    public const string StatIsLossless = "Whether decoding gives back the source exactly: the file has no bit limit or hybrid target.";
    public const string StatIsHybrid = "Whether the audio is coded in hybrid mode, which version 3 introduced: the header has a hybrid target and the new high flag. The main file is lossy, and a correction file would add what it lacks.";
    public const string StatIsRaw = "Whether the source was raw audio: the raw flag is set and the file has no WAV header.";
    public const string StatIsNoiseShaped = "Whether the encoder shaped the noise of the lossy coding by a first-order filter (the noise shaping flag).";
    public const string StatMode = "The speed mode the flags show: very fast, fast, normal or high. Version 1 and 2 files have one mode, which the flags rule calls fast.";
    public const string StatTargetBits = "The bitrate the hybrid encoder aimed at, in bits per sample and channel: the header's target over 256 and the channels.";
    public const string StatTargetRate = "The target bitrate over all channels: the header's target times the sample rate over 256,000.";
    public const string StatLossyBitLimit = "The bits the older lossy coding may spend on the size of one sample, as the header states it.";
    public const string StatStereoCoding = "How the stream codes its two channels: as left and right, as mid and side, or, where the flags leave the choice to the encoder sample by sample, adaptively. The sum and difference of versions 1 and 2 are mid and side.";
    public const string StatSourceExtension = "The extension of the file the audio came from, from the header.";
    public const string StatAverageBitrate = "The bits of the bitstream over the time its samples last. Without a decoder the bitstream ends at the trailer where one is found and else at the end of the file.";

    public const string Encoder = "The encoder that wrote the stream, as far as the header tells it, with the evidence and the confidence in it.";
    public const string ConfidenceMedium = "How sure the analyzer is: medium, since the version field names the generation of the program that wrote the file, but no release, and other software writes a version 3 header too.";
    public const string EncoderEvidence = "The version field says which generation of WavPack wrote the file; libwavpack reports it as the major version of the program. The Cool Edit filter of the 3.97 sources writes a version 3 header as well, from a header it builds (cool_wv.c).";

    public const string CorrectionBitstream = "The correction file of a version 3 hybrid file: a bitstream with no header or signature, read bit by bit beside the main file's. For each sample it holds where the sample lay in the range the hybrid coding left (3.97 wavpack.c; libwavpack 5.6.0 unpack3.c, get_word4), so only decoding both files reads it.";
    public const string CorrectionStream = "The correction bitstream of a version 3 hybrid file, which has no structure of its own to report.";

    public const string FlagMono = "One channel.";
    public const string FlagFast = "The fast mode: non-adaptive prediction and sum and difference stereo in the lossless files (3.0's -f, 3.4's -ff).";
    public const string FlagRaw = "A raw file: no WAV header before the WavPack header.";
    public const string FlagCalcNoise = "Never stored: an encoder flag for the noise calculation of the lossy mode, which a decoder refuses together with the extreme flag.";
    public const string FlagHigh = "The high mode's decoder path (the default lossless mode of 3.97, and -h).";
    public const string FlagBytes3 = "The samples take 3 bytes (17 to 24 bits).";
    public const string FlagOver20 = "A lossless file of more than 20 bits: the 4 lowest bits are coded apart.";
    public const string FlagWvc = "Never stored: a decoder sets it when it has a correction file; with the extreme flag a reader refuses the file if it is stored.";
    public const string FlagLossyShape = "First-order noise shaping of the lossy coding, the default from a sample rate of 64 kHz.";
    public const string FlagVeryFast = "Never stored: a flag of an earlier encoder, which a decoder refuses together with the extreme flag.";
    public const string FlagNewHigh = "The new high mode; with a hybrid target the file is in hybrid mode.";
    public const string FlagCancelExtreme = "With the extreme flag it cancels it: 3.97 stores the cross-channel flag without the extreme one as both set together, because it ran out of flag bits (wavpack.c).";
    public const string FlagCrossDecorr = "Cross-channel decorrelation. It counts only with the extreme flag; a decoder clears it otherwise.";
    public const string FlagNewDecorr = "The decorrelator of 7 terms.";
    public const string FlagJointStereo = "Mid and side stereo in the high mode and the hybrid mode.";
    public const string FlagExtreme = "The decorrelator of 18 terms (-h), or with the cancel flag the cross-channel flag alone.";
}

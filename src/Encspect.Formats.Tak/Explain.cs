namespace Encspect.Formats.Tak;

// What each TAK node is, in the words the report shows next to it. Constants only. The sources are takStream.pas of the TAK 2.3.3 SDK (the author's own source of the container),
// the SDK's documentation (tak_deco_lib.h and the Doc pages) and the files Takc.exe wrote in Encspect's tests; where a sentence states what those files show, it says so.
internal static class Explain
{
    public const string Stream = "A TAK file: the stream ID, metadata objects each protected by a CRC-24, and the audio frames (takStream.pas). The analyzer reads the metadata; the frames are counted as one run of bytes.";
    public const string StreamId = "The 4 characters \"tBaK\" that every TAK file starts with (takStream.pas, tak_str_StreamId).";

    public const string ObjectId = "The kind of metadata object, 7 bits: 0 End, 1 stream info, 2 seek table, 3 wave data, 4 encoder info, 5 padding, 6 MD5, 7 last frame info (takStream.pas).";
    public const string ObjectReserved = "A bit takStream.pas says is always 0.";
    public const string ObjectSize = "The number of bytes after the 4-byte object header. In Encspect's test files it counts the CRC-24 that ends the object, though takStream.pas says it covers the data without the CRC.";
    public const string ObjectCrc = "The CRC-24 the object stores in its last 3 bytes, over the data before it (RFC 2440's CRC-24: initial value 0xB704CE, polynomial 0x864CFB). The analyzer compares it in every analysis.";
    public const string ObjectComputedCrc = "The CRC-24 the analyzer computed over the object's data before the stored CRC-24.";
    public const string ObjectIsCrcValid = "Whether the stored CRC-24 of the object equals the one computed.";

    public const string StreamInfoObject = "The stream info: the codec and profile, the frame size, the number of samples and the audio format (takStream.pas, \"Stream-Info\"). Every TAK file must start with it.";
    public const string SeekTableObject = "The seek table that TAK versions before 1.1.1 wrote: a header and a list of seek points (takStream.pas). The analyzer shows its size and compares its CRC-24 and does not read the points.";
    public const string SeekTableData = "The seek table's header and seek points, which the analyzer does not read.";
    public const string WaveDataObject = "The header and footer of the WAV file that was compressed, kept so that a decoder can give the file back (takStream.pas, \"SimpleWaveData\"). Takc keeps up to 1 MiB of it by default.";
    public const string EncoderInfoObject = "The version of the encoder, its preset and its evaluation level (takStream.pas, \"Encoder-Info\"). Files of TAK 1.0 have none.";
    public const string PaddingObject = "Unused space in the metadata: Takc reserves it when it encodes from a pipe and does not know the size of the wave data yet. In Encspect's test files it holds zero bytes and has no CRC-24.";
    public const string PaddingData = "The padding bytes, zero in Encspect's test files.";
    public const string Md5Object = "The MD5 checksum of the raw audio data of the source, added by Takc's -md5 option (takStream.pas, \"Md5\").";
    public const string LastFrameInfoObject = "Where the last frame lies, counted from the first frame, and its size (takStream.pas, \"LastFrameInfo\"), so that a reader finds the end of the audio without walking the frames.";
    public const string EndObject = "The end of the metadata: a header with size 0, no data and no CRC-24. The first frame follows it.";
    public const string UnknownObject = "An object whose ID takStream.pas does not define. The walk skips it by its size.";
    public const string UnknownData = "The data of an object with an ID takStream.pas does not define.";

    public const string Codec = "The codec: 0 for TAK 1.0, 2 for TAK 2.0, 3 for the dedicated lossyWAV codec of the 2.1 betas, 4 for TAK 2.2 with more than two channels; 1 is \"Experimental!\" (the names of Takc's readme and tak_GetCodecName).";
    public const string Profile = "The profile, 0 to 4: the decoder's processing and memory needs. It equals the preset when the encoder's standard settings are used; a Turbo preset with 128 predictors stores the High profile (the SDK's documentation).";
    public const string FrameSizeType = "How the frame length is given: as a time (94, 125, 188 or 250 ms, worked out at the sample rate) or as a number of samples (512 to 16384). Types 10 to 15 are reserved.";
    public const string FrameLength = "The samples of each channel in a frame, worked out from the frame size type and the sample rate (takStream.pas, tak_str_CalcFrameSizeFromType).";
    public const string SampleCount = "The number of samples of each channel in the whole stream, 35 bits. 0 would mean the value is taken from another place, which TAK does not use.";
    public const string DataType = "The type of the audio data: 0 is PCM, the only one takStream.pas defines.";
    public const string SampleRate = "The sample rate in Hz, stored less 6000 in 18 bits.";
    public const string BitsPerSample = "The bits of each sample, stored less 8 in 5 bits: 8 to 24.";
    public const string Channels = "The number of channels, stored less 1 in 4 bits: 1 to 16. TAK versions before 2.2 stored it in 5 bits with no extension flag.";
    public const string HasExtension = "Whether the fields for more than two channels follow: the valid bits per sample and, where it says so, the speaker assignment (TAK 2.2).";
    public const string ValidBits = "The bits of each sample that hold audio, taken from the wave header (the count of the top sample bits as in WAVEFORMATEXTENSIBLE).";
    public const string HasSpeakerAssignment = "Whether a speaker is assigned to each channel.";
    public const string SpeakerAssignment = "The speaker of each channel, 6 bits each: 1 front left, 2 front right, 3 front center, 4 low frequency and so on up to 18 top back right, the order of the bits of a WAVEFORMATEXTENSIBLE channel mask.";
    public const string Speaker = "The speaker this channel feeds.";

    public const string HeaderSize = "The number of bytes of the WAV file's header that are kept, 3 bytes.";
    public const string FooterSize = "The number of bytes of the WAV file's footer that are kept, 3 bytes.";
    public const string HeaderData = "The first bytes of the WAV file that was compressed, up to its sample data, kept so that decoding can give the file back. The analyzer reads the chunks of a RIFF header, the format of a WAVE header and lossyWAV's fact chunk.";
    public const string TerminatingData = "The last bytes of the WAV file that was compressed, after its sample data, such as chunks after the data chunk.";
    public const string FactChunk = "The fact chunk of the stored WAV header. lossyWAV writes its version, the time of the run and its settings in it as text.";
    public const string ChunkList = "The RIFF chunks the stored data holds, listed by their IDs and sizes.";
    public const string Chunk = "One RIFF chunk: its ID, its size and its data.";
    public const string ChunkId = "The 4 characters that name the chunk.";
    public const string ChunkSize = "The size of the chunk's data in bytes.";
    public const string FormatTag = "The format tag of the fmt chunk in the stored WAV header, wFormatTag: 1 is PCM, 3 IEEE floating point, 0xFFFE the extensible format (mmreg.h). The analyzer reads the chunk only for its channels and channel mask.";
    public const string WavChannels = "The number of channels in the fmt chunk of the stored WAV header.";
    public const string ChannelMask = "Which speaker positions the channels of a WAVE_FORMAT_EXTENSIBLE header feed, one bit for each position.";
    public const string Speakers = "The speakers the channel mask names.";

    public const string EncoderVersion = "The version of the encoder that wrote the file, three digits of 8 bits each, the major version in the top one.";
    public const string Preset = "The preset, 0 to 4 (fastest to strongest), which is the profile the encoder was set to.";
    public const string Evaluation = "The evaluation level, which changes only the encoder's search and not the decoding: Standard, Extra or Max. A value of 3 is not defined.";
    public const string EncoderReserved = "Two bits takStream.pas calls reserved.";

    public const string Md5Digest = "The digest as 32 hexadecimal digits, over the raw audio data of the source. The analyzer does not compare it, which needs a decoder.";
    public const string LastFramePosition = "The distance of the last frame from the first frame in bytes, 5 bytes.";
    public const string LastFrameSize = "The size of the last frame in bytes, 3 bytes.";

    public const string AudioData = "The audio frames, from the first frame to the end of the stream. Each frame has a header with a CRC-24 and a CRC-24 over its coded data; the analyzer does not walk them.";
    public const string Trailing = "Bytes after the last frame that belong to no tag; the analyzer does not read them.";
    public const string Unread = "Bytes the walk did not read.";

    public const string Encoder = "The program that wrote the file, as the encoder info object shows it, with the evidence and the confidence in it.";
    public const string ConfidenceHigh = "High: the encoder info object states the version of the encoder that wrote the file.";
    public const string ConfidenceMedium = "Medium: the version is inferred from a missing object, not stated: the SDK's decoder takes version 1.0.0 for a file without an encoder info object.";
    public const string Evidence = "Something in the file that points to the encoder.";

    public const string Statistics = "Figures computed over the metadata of the file.";
    public const string StatCodec = "The codec from the stream info.";
    public const string StatProfile = "The profile from the stream info: the decoder's processing and memory needs (the SDK's documentation).";
    public const string StatPreset = "The preset as Takc writes it: -p and the level, then e for the evaluation Extra or m for Max. Without an encoder info object it is the profile with the evaluation unknown.";
    public const string StatFrameLength = "The samples of each channel in a frame, from the stream info.";
    public const string StatSamples = "The samples of each channel the stream info states.";
    public const string StatDuration = "The duration of the samples at the sample rate.";
    public const string StatRate = "The sample rate of the audio, from the stream info.";
    public const string StatChannels = "The number of channels, from the stream info.";
    public const string StatBits = "The bits of each sample, from the stream info.";
    public const string StatLayout = "The speakers the channels feed: the speaker assignment of the stream info where it has one, else the channel mask of the stored WAV header, else center for one channel and left and right for two.";
    public const string StatHeaderChecksums = "The metadata objects whose CRC-24 equals the one computed over the object's data.";
    public const string StatHeaderChecksumsFailed = "The metadata objects whose CRC-24 differs from the one computed.";
    public const string AverageBitrate = "The bits of the audio data over the time its samples last.";
}

namespace Encspect.Formats.Flac;

// The background articles the FLAC nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Stream = "flac-stream";
    public const string MetadataBlocks = "flac-metadata-blocks";
    public const string StreamInfo = "flac-streaminfo";
    public const string SeekTable = "flac-seek-table";
    public const string CueSheet = "flac-cuesheet";
    public const string Picture = "flac-picture";
    public const string Frames = "flac-frames";
    public const string BlockingStrategy = "flac-blocking-strategy";
    public const string ChannelAssignment = "flac-channel-assignment";
    public const string Subframes = "flac-subframes";
    public const string LinearPrediction = "linear-prediction";
    public const string RiceCoding = "rice-coding";
    public const string WastedBits = "wasted-bits";
    public const string Ogg = "flac-in-ogg";
    public const string MidSideStereo = "mid-side-stereo";
    public const string Crc = "crc";
    public const string Verification = "verification";
    public const string VorbisComments = "vorbis-comments";
}

// What each FLAC node is, in the words the report shows next to it. Constants only: the frames of a long file are built by the hundred
// thousand, so a description picks one of these and never allocates. The source is RFC 9639 (FLAC), cited by section where the section
// is known, and the files the fixtures' encoders wrote.
internal static class Explain
{
    public const string Stream = "The FLAC stream: the marker, the metadata blocks and then the frames of audio, one after another (RFC 9639).";
    public const string Marker = "The four characters \"fLaC\" that begin a native FLAC stream (RFC 9639).";
    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies between the FLAC frames, which the tag breaks; the analyzer does not read them as frames.";
    public const string SkippedData = "Bytes between or around frames that hold no frame; the analyzer looks for the next frame whose header and CRC-16 hold, and a frame header only counts when another follows it.";

    public const string MappingHeader = "The start of the first Ogg packet of a FLAC stream: the packet type, the signature, the version of the mapping, the number of header packets and the \"fLaC\" marker (RFC 9639, section 10.1).";
    public const string OggPacketType = "The packet type, 0x7F, that marks the first packet of a FLAC stream in Ogg.";
    public const string OggSignature = "The characters \"FLAC\" that follow the packet type.";
    public const string OggMajorVersion = "The major version of the Ogg mapping, which is 1.";
    public const string OggMinorVersion = "The minor version of the Ogg mapping, which is 0.";
    public const string OggHeaderCount = "The number of header packets that follow the first, each holding one metadata block.";
    public const string OggHeaderCountUnknown = "The field holds 0, which means the number of header packets is not stated; the last-block flag of the metadata blocks ends them.";
    public const string TrailingPacketData = "Bytes in a packet after the end of its frame.";
    public const string MetadataBlock = "One metadata block: a header of four bytes and the data whose length it states. The first block is STREAMINFO (RFC 9639, section 8).";
    public const string BlockHeader = "The four bytes that start a metadata block: whether it is the last one, its type and the length of its data (RFC 9639, section 8).";
    public const string IsLastBlock = "Whether this is the last metadata block; the first frame follows it.";
    public const string BlockType = "The type of the block: 0 STREAMINFO, 1 PADDING, 2 APPLICATION, 3 SEEKTABLE, 4 VORBIS_COMMENT, 5 CUESHEET, 6 PICTURE, 7 to 126 reserved, and 127, which the specification forbids.";
    public const string BlockLength = "The length of the block's data in bytes, not counting the four bytes of the header.";
    public const string FurtherMetadataBlocks = "More metadata blocks, which the report reads but does not list one by one.";
    public const string ReservedBlockData = "The data of a block whose type the specification reserves; decoders skip it.";
    public const string ForbiddenBlockData = "The data of a block with the forbidden type 127.";

    public const string StreamInfoShort = "The bytes of a STREAMINFO block that the data ends inside, or that are fewer than the 34 the block needs.";
    public const string MinBlockSize = "The smallest block size used in the stream, in samples, not counting the last block (RFC 9639, section 8.2).";
    public const string MaxBlockSize = "The largest block size used in the stream, in samples (RFC 9639, section 8.2).";
    public const string MinFrameSize = "The size in bytes of the smallest frame in the stream; 0 means the encoder did not know it (RFC 9639, section 8.2).";
    public const string MaxFrameSize = "The size in bytes of the largest frame in the stream; 0 means the encoder did not know it (RFC 9639, section 8.2).";
    public const string StreamInfoSampleRate = "The sample rate in Hz; frames whose header does not state a rate use it (RFC 9639, section 8.2).";
    public const string StreamInfoChannels = "The number of channels, 1 to 8; the field stores the count less 1 (RFC 9639, section 8.2).";
    public const string StreamInfoBits = "The bits of each sample; the field stores the count less 1, and frames whose header does not state a size use it (RFC 9639, section 8.2).";
    public const string TotalSamples = "The number of samples in each channel of the stream (RFC 9639, section 8.2).";
    public const string TotalSamplesUnknown = "The field holds 0, which means the encoder did not know the number of samples (RFC 9639, section 8.2).";
    public const string Md5 = "The MD5 of all the samples, interleaved, signed and little-endian, each rounded up to whole bytes (RFC 9639, section 8.2). It covers decoded audio, so the analyzer compares it only when the stream is decoded.";
    public const string Md5NotStored = "The MD5 field holds 0, which means the encoder did not know it (RFC 9639, section 8.2), so there is nothing to compare the decoded audio with.";

    public const string Padding = "Bytes that hold nothing, left so that other metadata can grow without rewriting the audio; the specification has them all 0 (RFC 9639, section 8).";
    public const string ApplicationShort = "The bytes of an application block that are fewer than the four of its application ID.";
    public const string ApplicationId = "The ID of the application that wrote the block, four characters (RFC 9639, section 8).";
    public const string ApplicationData = "The application's own data, which only that application reads.";

    public const string SeekPoints = "The seek points of the table, in the order of the stream: the sample number, the byte offset of the frame that holds it and the samples in that frame (RFC 9639, section 8).";
    public const string SeekPoint = "One seek point: 18 bytes that tell a player which frame to start from to reach a sample.";
    public const string SeekPlaceholder = "A placeholder seek point, whose sample number is all ones; placeholders reserve room in the table and belong at its end.";
    public const string SeekSampleNumber = "The number of the first sample of the target frame.";
    public const string SeekOffset = "The offset in bytes of the target frame from the first byte of the first frame.";
    public const string SeekFrameSamples = "The number of samples in the target frame.";
    public const string FurtherSeekPoints = "More seek points, which the report does not list one by one.";

    public const string CommentData = "The bytes of a Vorbis comment, which no tag parser read.";
    public const string CueSheetShort = "The bytes of a cue sheet that are fewer than the 396 of its fixed fields.";
    public const string CueCatalog = "The media catalog number, 128 bytes of ASCII padded with zeros; for a compact disc the 13-digit UPC or EAN (RFC 9639, section 8).";
    public const string CueLeadIn = "The number of lead-in samples, which precede the first track on a compact disc.";
    public const string CueIsCompactDisc = "Whether the cue sheet describes a compact disc.";
    public const string CueTrackCount = "The number of tracks in the cue sheet, the lead-out track included.";
    public const string CueTrack = "One track: where it starts, its number, its ISRC, whether it is audio and has pre-emphasis, and its index points.";
    public const string CueTrackOffset = "The offset of the track's first sample from the start of the audio.";
    public const string CueTrackNumber = "The track number; 170 is the lead-out track of a compact disc and 255 that of any other medium.";
    public const string CueIsrc = "The track's International Standard Recording Code, twelve characters.";
    public const string CueIsAudio = "Whether the track holds audio; the field stores the opposite.";
    public const string CuePreEmphasis = "Whether the track was recorded with pre-emphasis.";
    public const string CueIndexCount = "The number of index points of the track.";
    public const string CueIndexPoint = "One index point: an offset from the track's start and its number.";
    public const string CueIndexOffset = "The offset of the index point from the first sample of its track.";
    public const string CueIndexNumber = "The number of the index point; 0 or 1 on a compact disc mark the pre-gap and the start of the track.";

    public const string PictureShort = "The bytes of a picture block that its fields do not account for, or that are fewer than the fields need.";
    public const string PictureType = "What the picture shows, by the list that ID3v2's attached picture frame uses (RFC 9639, section 8).";
    public const string PictureMime = "The MIME type of the picture data, or the string \"-->\" where the data is a URL, with its length.";
    public const string PictureDescription = "A description of the picture in UTF-8, with its length.";
    public const string PictureWidth = "The width of the picture in pixels.";
    public const string PictureHeight = "The height of the picture in pixels.";
    public const string PictureDepth = "The color depth of the picture in bits per pixel.";
    public const string PictureColors = "The number of colors of an indexed picture, or 0 for one that is not indexed.";
    public const string PictureDataLength = "The length of the picture data in bytes.";
    public const string PictureData = "The picture itself, in the format the MIME type names; the analyzer does not decode it.";

    public const string Frame = "One frame: a header, a subframe for each channel and a CRC-16, holding the same number of samples of every channel (RFC 9639, section 9).";
    public const string FrameHeader = "The frame header: the sync code, blocking strategy, block size, sample rate, channel assignment, sample size, the frame or sample number and a CRC-8 (RFC 9639, section 9.1).";
    public const string SyncCode = "The 14-bit sync code 0b11111111111110 that starts every frame (RFC 9639, section 9.1).";
    public const string ReservedBit = "A bit the specification reserves; it is 0.";
    public const string StrategyFixed = "The stream uses one block size, so the header counts frames (RFC 9639, section 9.1).";
    public const string StrategyVariable = "The stream uses blocks of different sizes, so the header counts samples (RFC 9639, section 9.1).";
    public const string BlockSize192 = "The number of samples in each channel of the frame: 192, which the code 1 names.";
    public const string BlockSizeLong = "The number of samples in each channel of the frame: 576 times a power of two, which the codes 2 to 5 name.";
    public const string BlockSizePowerOfTwo = "The number of samples in each channel of the frame: 256 times a power of two, which the codes 8 to 15 name.";
    public const string BlockSizeStated8 = "The number of samples in each channel of the frame, which an 8-bit field after the number states, less 1.";
    public const string BlockSizeStated16 = "The number of samples in each channel of the frame, which a 16-bit field after the number states, less 1.";
    public const string SampleRateStreamInfo = "The sample rate of the frame; the code 0 leaves it to STREAMINFO.";
    public const string SampleRateCoded = "The sample rate of the frame, one of the common rates a 4-bit code names.";
    public const string SampleRateStated = "The sample rate of the frame, which a field after the number states: in kHz as 8 bits, in Hz or in tens of Hz as 16 bits.";
    public const string AssignmentIndependent = "How the channels are coded: each one by itself, the code being the number of channels less 1 (RFC 9639, section 9.1).";
    public const string AssignmentDecorrelated = "How the two channels are coded: as left and side, side and right, or mid and side, where the side channel is the difference of left and right (RFC 9639, section 9.1).";
    public const string SampleSizeStreamInfo = "The bits of each sample of the frame; the code 0 leaves them to STREAMINFO.";
    public const string SampleSizeCoded = "The bits of each sample of the frame, which a 3-bit code names.";
    public const string FrameNumber = "The number of the frame, counted from 0, in a stream of one block size; it is coded like a UTF-8 character that may take up to six bytes (RFC 9639, section 9.1).";
    public const string SampleNumber = "The number of the frame's first sample in a stream of changing block sizes; it is coded like a UTF-8 character that may take up to seven bytes (RFC 9639, section 9.1).";
    public const string UncommonBlockSize = "The block size less 1, stated after the number because no 4-bit code names it.";
    public const string UncommonSampleRate = "The sample rate, stated after the number because no 4-bit code names it.";
    public const string Crc8 = "The CRC-8 of the header's bytes before it, which holds; the analyzer compares it in every analysis (RFC 9639, section 9.1.8).";
    public const string Crc8Differs = "The CRC-8 the header stores, which differs from the one computed over its other bytes (RFC 9639, section 9.1.8). The analyzer compares it in every analysis.";

    public const string Subframe = "The subframe of one channel: its type, the wasted bits, and either the samples themselves or a predictor with its warm-up samples and the residual that corrects it (RFC 9639, section 9.2).";
    public const string SubframeType = "How the subframe codes its samples: constant, verbatim, a fixed predictor of order 0 to 4, or a linear predictor of order 1 to 32 (RFC 9639, section 9.2).";
    public const string PredictorOrder = "The order of the predictor: how many earlier samples it uses to predict the next one.";
    public const string WastedBits = "The number of low bits that are zero in every sample of the subframe and are left out of it; the decoder shifts the samples back up.";
    public const string NoWastedBits = "No wasted bits: the flag that announces them is 0.";
    public const string ConstantSample = "The one value every sample of the subframe has.";
    public const string VerbatimSamples = "All the samples of the subframe, stored as they are.";
    public const string WarmUpSamples = "The first samples of the subframe, stored as they are because the predictor has no earlier ones to work from.";
    public const string CoefficientPrecision = "The bits of each predictor coefficient.";
    public const string CoefficientShift = "The number of bits the predictor's sum is shifted right by.";
    public const string Coefficients = "The coefficients of the linear predictor, in two's complement, the first applying to the sample just before the one predicted.";
    public const string Residual = "The residual: what is left when the prediction is taken from each sample, coded with Rice codes in partitions (RFC 9639, section 9.2).";
    public const string MethodRice = "The residual coding method: Rice codes whose parameter is 4 bits.";
    public const string MethodRice2 = "The residual coding method: Rice codes whose parameter is 5 bits.";
    public const string PartitionOrder = "The number of partitions is 2 to this power; each holds the residual samples of an equal share of the block.";
    public const string Partitions = "The partitions of the residual, each with the Rice parameter its samples are coded with.";
    public const string PartitionRice = "One partition, whose samples are coded with a Rice code of the parameter.";
    public const string PartitionEscaped = "One partition whose samples are not Rice coded: the escape code stands in place of the parameter, and each sample is stored with a fixed number of bits.";
    public const string RiceParameter = "The Rice parameter of the partition: how many low bits of each folded residual are stored as they are, after the quotient in unary.";
    public const string EscapeCode = "The parameter field, set to all ones: the escape code that marks a partition of samples stored with a fixed width.";
    public const string EscapedSampleBits = "The number of bits of each sample of the escaped partition; 0 means every sample is 0.";
    public const string FurtherPartitions = "More partitions, which the report does not list one by one.";
    public const string ParameterRange = "The smallest and largest Rice parameter among the partitions that are Rice coded.";
    public const string EscapedPartitions = "The partitions of this residual that are stored with a fixed width instead of Rice coded.";
    public const string UndetailedSubframes = "The subframes of the frame, which the report lists only for the first 4,096 frames. They are read and counted in every frame.";
    public const string CutFrameData = "The rest of the frame, which the data ends inside.";
    public const string UnreadFrameData = "The rest of the frame, which cannot be read as subframes; the frame ends where the next frame was found.";
    public const string Crc16 = "The CRC-16 the frame stores over all its bytes before it, which the analyzer compares in every analysis (RFC 9639, section 9.3).";
    public const string ComputedCrc16 = "The CRC-16 the analyzer computed over the frame's bytes before the CRC-16.";
    public const string IsCrc16Valid = "Whether the stored CRC-16 equals the computed one.";

    public const string Statistics = "Figures computed over the frames of the stream.";
    public const string StatFrames = "The number of frames in the stream, a frame the data cuts short included.";
    public const string StatSamples = "The samples of each channel the frames hold, added up; a frame the data cuts short is not counted.";
    public const string StatDuration = "The duration of the samples at the sample rate.";
    public const string StatRate = "The sample rate of the audio, from STREAMINFO or, without it, the first frame.";
    public const string StatChannels = "The number of channels, from STREAMINFO or, without it, the first frame.";
    public const string StatLayout = "The speakers the channels feed, in the order the specification gives for one to eight channels (RFC 9639, section 9.1).";
    public const string StatBits = "The bits of each sample, from STREAMINFO or, without it, the first frame.";
    public const string StatStrategy = "Whether the frames count in whole frames (fixed block size) or in samples (variable block size), or both.";
    public const string StatSmallestBlock = "The smallest block size of any frame, in samples.";
    public const string StatLargestBlock = "The largest block size of any frame, in samples.";
    public const string StatIndependent = "The stereo frames whose two channels are coded each by itself.";
    public const string StatLeftSide = "The stereo frames coded as the left channel and the side channel, the difference of left and right.";
    public const string StatSideRight = "The stereo frames coded as the side channel and the right channel.";
    public const string StatMidSide = "The stereo frames coded as the mid channel, the average of left and right, and the side channel.";
    public const string StatSubframeTypes = "How many subframes of each type the frames hold.";
    public const string StatConstant = "The subframes that hold one value for all their samples.";
    public const string StatVerbatim = "The subframes that store their samples as they are.";
    public const string StatFixed = "The subframes coded with one of the five fixed predictors.";
    public const string StatLpc = "The subframes coded with a linear predictor with its own coefficients.";
    public const string StatAverageOrder = "The average order of the linear predictors.";
    public const string StatHighestOrder = "The highest order of any linear predictor.";
    public const string StatPrecision = "The range of bits per predictor coefficient among the linear predictors.";
    public const string StatWasted = "The subframes whose samples all have low bits that are zero.";
    public const string StatPartitionOrders = "How many residuals use each partition order.";
    public const string StatPartitionOrder = "The residuals that use this partition order.";
    public const string StatRice2 = "The residuals coded with 5-bit Rice parameters instead of 4-bit ones.";
    public const string EscapedPartitionsStat = "The partitions that are stored with a fixed width instead of Rice coded, over all the residuals.";
    public const string StatHeaderChecksums = "The frames whose header CRC-8 equals the one computed over the header.";
    public const string StatHeaderChecksumsFailed = "The frames whose header CRC-8 differs from the one computed.";
    public const string StatFrameChecksums = "The frames whose CRC-16 equals the one computed over the frame.";
    public const string StatFrameChecksumsFailed = "The frames whose CRC-16 differs from the one computed.";
    public const string StatMd5 = "The MD5 of the audio that STREAMINFO stores, or \"not stored\" where it holds 0.";

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

    public const string Encoder = "The encoder that wrote the stream, as far as the Vorbis comment names it, with the evidence and the confidence in it.";
    public const string Confidence = "How sure the analyzer is: high where the vendor string has the form libFLAC writes, medium where only an ENCODER comment names ffmpeg's encoder or the vendor string is in libFLAC's form but of no release the analyzer knows.";
    public const string Evidence = "Something in the file that points to the encoder.";
}

namespace Encspect.Formats.Aiff;

// What each AIFF node is, in the words the report shows next to it. Constants only. The sources are Apple's "Audio Interchange File Format AIFF-C"
// draft of 8/26/91 (the AIFF-C specification, cited by section) and "Audio Interchange File Format: AIFF, version 1.3", and for the compression
// types it does not list, Apple's QuickTime File Format Specification and Core Audio Format Specification (see AifcType).
internal static class Explain
{
    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies inside the FORM chunk, which breaks the chain of chunks; the analyzer does not read them as chunks.";
    public const string TrailingData = "Bytes after the end of the FORM chunk that no tag parser recognized.";
    public const string UnreadData = "Bytes where a chunk should start but no chunk header does: its ID is not four printable characters. The analyzer does not read them.";
    public const string FurtherChunks = "More chunks, which the report reads no further: it lists at most 2,048 chunks.";
    public const string ExtraData = "Bytes after the fields of the chunk, which the specification does not use.";

    public const string FormAiff = "The FORM chunk that holds the whole file, of the form type AIFF: uncompressed audio (AIFF 1.3, \"File Structure\").";
    public const string FormAifc = "The FORM chunk that holds the whole file, of the form type AIFC: audio that may be compressed (AIFF-C, section 2.0).";
    public const string FormId = "The first four bytes of the file: FORM, the container of the EA IFF 85 format that AIFF is built on.";
    public const string FormSize = "The size of the FORM chunk's data in bytes: the form type and all the chunks, not the ID and this field. A writer that cannot go back leaves 0 or 0xFFFFFFFF.";
    public const string FormType = "The form type: AIFF for uncompressed audio, AIFC for AIFF-C, whose Common Chunk adds a compression type.";
    public const string ChunkId = "The ID of the chunk: four ASCII characters from the space to the tilde (AIFF-C, section 1.0).";
    public const string ChunkSize = "The size of the chunk's data in bytes, not counting the ID or this field or the pad byte (AIFF-C, section 2.0).";
    public const string PadByte = "A byte of zero after a chunk whose size is odd, which keeps every chunk at an even offset (AIFF-C, section 2.0).";
    public const string StringPad = "The pad byte that makes a Pascal-style string, with its count byte, an even number of bytes, shown as the file has it; the specification has it zero (AIFF-C, \"pstring\").";
    public const string ChunksNotShown = "More chunks, which the report reads no further: its tree holds at most 250,000 nodes.";
    public const string UnknownChunk = "A chunk the analyzer does not read. A reader skips the chunks it does not know (AIFF-C, section 2.0).";

    public const string FverChunk = "The Format Version Chunk: the date of the AIFF-C rules the file follows. An AIFC file holds exactly one (AIFF-C, section 3.0).";
    public const string FverTimestamp = "The version as the seconds since January 1, 1904 at which Apple issued it; version 1 of AIFF-C is 2726318400. A reader compares it exactly (AIFF-C, section 3.0).";
    public const string CommChunk = "The Common Chunk: the number of channels, the length, the sample size and the sample rate of the sound, and in AIFF-C its compression (AIFF-C, section 4.0). Every FORM holds exactly one.";
    public const string CommChannels = "The number of audio channels, numChannels: 1 is mono, 2 stereo (AIFF-C, section 4.0).";
    public const string CommFrames = "The number of sample frames, numSampleFrames: sample points per channel, not bytes. The clearest statement of the length (AIFF-C, \"Number of Sample Frames\").";
    public const string CommSampleSize = "The number of bits in each sample point of the uncompressed sound, sampleSize, 1 to 32; for compressed sound the bits before compression (AIFF-C, section 4.0).";
    public const string CommRate = "The sample rate in sample frames per second, sampleRate: an 80-bit IEEE 754 extended number (AIFF-C, section 4.0).";
    public const string RateExponent = "The sign bit and the 15-bit exponent of the extended number; the exponent has a bias of 16383.";
    public const string RateMantissa = "The 64-bit mantissa of the extended number, whose first bit is explicit; the value is the mantissa times 2 to the exponent less 16383 less 63.";
    public const string CommType = "The compression type, compressionType: NONE for uncompressed sound, other IDs for compressed or differently coded sound (AIFF-C, section 4.0).";
    public const string CommNameLength = "The length of the compression name, the count byte of its Pascal-style string.";
    public const string CommName = "The compression name, compressionName: text for people, where the type is for programs (AIFF-C, section 4.0).";

    public const string SsndChunk = "The Sound Data Chunk: the sample frames (AIFF-C, section 5.0). Every FORM with sound holds one.";
    public const string SsndOffset = "Where the first sample frame starts in the sound data, in bytes, offset; 0 unless the data is aligned to disk blocks (AIFF-C, section 5.0).";
    public const string SsndBlockSize = "The size of the blocks the sound data is aligned to, blockSize; 0 if it is not (AIFF-C, section 5.0).";
    public const string SsndData = "The sound data: the sample frames, in the coding the Common Chunk names, after the offset (AIFF-C, section 5.0).";

    public const string MarkChunk = "The Marker Chunk: positions in the sound, each with an ID and a name (AIFF-C, section 6.0).";
    public const string MarkCount = "The number of markers in the chunk, numMarkers.";
    public const string MarkTable = "The markers, which follow one another without gaps.";
    public const string Marker = "One marker: an ID, a position and a name (AIFF-C, section 6.0).";
    public const string MarkId = "The ID of the marker, which is above zero and unique in the FORM.";
    public const string MarkPosition = "The position of the marker in sample frames; the marker lies between two frames, and position 0 is before the first.";
    public const string MarkName = "The name of the marker, a Pascal-style string: a count byte, the text and a pad byte when the total would be odd.";
    public const string InstChunk = "The Instrument Chunk: what a sampling keyboard needs to play the sound as an instrument (AIFF-C, section 9.0). Its size is always 20.";
    public const string InstBaseNote = "The MIDI note number of the pitch the sound was recorded at, baseNote, 0 to 127; middle C is 60.";
    public const string InstDetune = "The tuning adjustment in cents, detune, -50 to 50.";
    public const string InstLowNote = "The lowest MIDI note the sound is suggested for, lowNote.";
    public const string InstHighNote = "The highest MIDI note the sound is suggested for, highNote.";
    public const string InstLowVelocity = "The lowest MIDI velocity the sound is suggested for, lowVelocity, 1 to 127.";
    public const string InstHighVelocity = "The highest MIDI velocity the sound is suggested for, highVelocity.";
    public const string InstGain = "The change of volume when the sound is played, in decibels; 0 means none.";
    public const string InstSustain = "The loop played while the instrument sustains the sound, sustainLoop.";
    public const string InstRelease = "The loop played while the instrument releases the sound, releaseLoop.";
    public const string LoopMode = "How the loop plays, playMode: 0 no looping, 1 forward, 2 forward and backward.";
    public const string LoopBegin = "The ID of the marker where the loop begins, beginLoop.";
    public const string LoopEnd = "The ID of the marker where the loop ends, endLoop.";
    public const string ComtChunk = "The Comments Chunk: comments with a date and optionally a marker they are about (AIFF-C, section 7.0).";
    public const string ComtCount = "The number of comments in the chunk, numComments.";
    public const string ComtTable = "The comments, which follow one another without gaps.";
    public const string Comment = "One comment: a time stamp, a marker ID and text (AIFF-C, section 7.0).";
    public const string ComtTimeStamp = "When the comment was made, as seconds since January 1, 1904.";
    public const string ComtMarker = "The ID of the marker the comment is about, or 0 for none.";
    public const string ComtLength = "The length of the text in bytes, count; the text is padded to an even size.";
    public const string ComtText = "The text of the comment.";

    public const string NameChunk = "The Name Chunk: the name of the sampled sound, as text (AIFF-C, section 13.0).";
    public const string AuthChunk = "The Author Chunk: the names of the authors of the sound, as text (AIFF-C, section 13.0).";
    public const string CopyrightChunk = "The Copyright Chunk: a date and the copyright owner, as text; the chunk ID stands for the copyright sign (AIFF-C, section 13.0).";
    public const string AnnoChunk = "The Annotation Chunk: a comment as text. AIFF-C discourages it in favor of the Comments Chunk (AIFF-C, section 13.0).";
    public const string TextData = "The text of the chunk, which has no terminator: its size gives its length.";
    public const string ApplChunk = "The Application Specific Chunk: data an application keeps for itself, tagged by its signature (AIFF-C, section 12.0).";
    public const string ApplSignature = "The signature of the application, applicationSignature; pdos for an Apple II program and stoc for one on other systems, which name themselves in a Pascal-style string.";
    public const string ApplName = "The name of the application, a Pascal-style string.";
    public const string ApplData = "The application's data, which the analyzer does not read.";
    public const string AesdChunk = "The Audio Recording Chunk: the 24 bytes of channel status data of an AES digital audio signal (AIFF-C, section 11.0).";
    public const string AesdData = "The AES channel status data, AESChannelStatusData.";
    public const string MidiChunk = "The MIDI Data Chunk: a stream of MIDI data, mainly system exclusive messages of instruments (AIFF-C, section 10.0).";
    public const string MidiData = "The MIDI data.";
    public const string Id3Chunk = "An ID3v2 tag stored in a chunk. The analyzer hands the chunk's data to the ID3v2 parser.";

    public const string Statistics = "Figures over the audio the file describes: its length, rate, channels and the bits of its samples.";
    public const string StatCodec = "The coding of the sound, from the compression type of the Common Chunk; uncompressed in a FORM AIFF.";
    public const string StatCompressionType = "The compression type of the Common Chunk.";
    public const string StatSampleFrames = "The number of sample frames: the Common Chunk's count, or, when the sound data holds fewer, the frames it holds.";
    public const string StatDuration = "The length of the audio: the sample frames divided by the sample rate.";
    public const string StatRate = "The sample rate in Hz, from the Common Chunk's extended number.";
    public const string StatChannels = "The number of channels, from the Common Chunk.";
    public const string StatLayout = "The speaker each channel feeds where the specification says: mono, stereo left and right, three channels left, right and center (AIFF-C, section 4.0).";
    public const string StatBits = "The bits of each sample: the Common Chunk's sample size, or 32 or 64 for the floating-point types.";
    public const string StatSampleFormat = "How a sample is coded: AIFF samples are signed integers (AIFF-C, section 5.0), the type raw is unsigned 8-bit, fl32 and fl64 are IEEE 754 numbers.";
    public const string StatByteOrder = "The order of the bytes of a sample: big-endian, except for the type sowt, which is little-endian.";
    public const string StatBitrate = "The average bitrate of the sound data: its bytes times 8 over the length of the audio.";
}

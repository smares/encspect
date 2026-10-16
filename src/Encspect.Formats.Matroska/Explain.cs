namespace Encspect.Formats.Matroska;

// What each node the parser makes itself is, apart from the elements the generated table describes (Elements.cs). Constants only,
// shared by every node of their kind, from RFC 9559 and RFC 8794.
internal static class Explain
{
    public const string File = "A Matroska or WebM file: an EBML document, whose EBML Header names the document type and whose Segment holds the tracks and their frames (RFC 9559, section 4.5).";
    public const string SegmentWithoutHeader = "The Top-Level Elements of a Segment whose own element header is missing, because the file starts after it (RFC 9559, section 4.5).";
    public const string UnknownElement = "An element whose ID the Matroska schema does not list; readers skip it by its size (RFC 8794, section 6.1; RFC 9559, section 4.4).";
    public const string UnparsedData = "Bytes that start no element the analyzer can read, so they are not read as elements.";
    public const string Computed = "The CRC-32 the analyzer computes over the Element Data of the CRC-32 element's parent after the CRC-32 element, to compare with the stored one (RFC 8794, section 11.3.1).";

    public const string TrackNumber = "The number of the track the block belongs to, a variable-size integer, which must match a TrackEntry's TrackNumber (RFC 9559, section 10).";
    public const string BlockTimestamp = "The block's timestamp as a signed 16-bit count of Track Ticks relative to the Cluster's Timestamp, the time of its first frame (RFC 9559, sections 10.1 and 11.2).";
    public const string BlockFlags = "The block's flags: for a SimpleBlock the keyframe, invisible, lacing and discardable bits, for a Block the invisible and lacing bits (RFC 9559, sections 10.1 and 10.2).";
    public const string FlagKeyframe = "Whether the SimpleBlock contains only keyframes (bit 0x80, RFC 9559, section 10.2).";
    public const string FlagReserved = "Reserved bits, which must be 0 (RFC 9559, sections 10.1 and 10.2).";
    public const string FlagInvisible = "Whether the codec should decode the frames but not display them (bit 0x08, RFC 9559, sections 10.1 and 10.2).";
    public const string FlagLacing = "How the block packs several frames: 0 none, 1 Xiph, 3 EBML, 2 fixed-size (bits 0x06, RFC 9559, section 10.3).";
    public const string FlagDiscardable = "Whether a player may discard the frames of the SimpleBlock when needed (bit 0x01, RFC 9559, section 10.2).";
    public const string FlagUnused = "The unused bit of a Block's flags (RFC 9559, section 10.1).";
    public const string LaceHeader = "The lacing header: the number of frames minus one and the sizes of all frames but the last, coded as the lacing mode says (RFC 9559, section 10.3).";
    public const string LaceFrameCount = "The number of frames in the lace minus 1 (RFC 9559, section 10.3).";
    public const string LaceSizes = "The sizes of the frames except the last, whose size is what the block has left (RFC 9559, section 10.3).";
    public const string Frame = "One frame of the block's track: the unit its codec decodes, such as an audio frame, an access unit or a video picture (RFC 9559, section 10.3).";
    public const string BlockData = "The bytes of the block after its header, which the analyzer does not read as frames.";

    public const string TrackFrames = "The frames of an audio track put together from the blocks that carry them, handed to the codec parser of the track's codec ID.";
}

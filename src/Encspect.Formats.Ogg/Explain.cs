namespace Encspect.Formats.Ogg;

// The background articles the Ogg nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string OggPages = "ogg-pages";
    public const string GranulePosition = "granule-position";
}

// What each Ogg node is. Constants only, shared by every node of their kind, from RFC 3533:
// section 4 for logical streams, grouping and chaining, section 5 for segments and lacing, and
// section 6 for the page header.
internal static class Explain
{
    public const string Ogg = "Ogg data: pages that carry the packets of one or more logical streams, each page naming its stream by a serial number (RFC 3533).";
    public const string Pages = "The pages and the gaps between them, in file order (RFC 3533, section 6).";
    public const string Page = "An Ogg page: a 27-byte header, the segment table, and a body that holds segments of its stream's packets (RFC 3533, section 6).";
    public const string Gap = "Bytes between pages that start no page whose CRC holds, which readers skip until the next capture pattern (RFC 3533, section 6).";
    public const string CapturePattern = "The capture pattern \"OggS\", which marks the start of a page and lets readers regain sync after damage (RFC 3533, section 6).";
    public const string UnparsedHeader = "The rest of a page whose header the file cuts short.";
    public const string Version = "The stream structure version, 0 for the format RFC 3533 specifies (section 6).";
    public const string HeaderType = "The header type flags: whether the page continues a packet, and whether it begins or ends its logical stream (RFC 3533, section 6).";
    public const string IsContinued = "Whether the page starts with the rest of a packet from its stream's previous page (bit 0x01).";
    public const string IsBeginningOfStream = "Whether this is the first page of its logical stream, which holds the packet that names the codec (bit 0x02).";
    public const string IsEndOfStream = "Whether this is the last page of its logical stream (bit 0x04).";
    public const string UndefinedFlags = "Header type bits above 0x04, which RFC 3533 does not define.";
    public const string GranulePosition = "A position the codec's mapping defines, such as a count of samples, at the end of the last packet that ends on this page (RFC 3533, section 6).";
    public const string GranuleNone = "-1: no packet ends on this page, so it has no position (RFC 3533, section 6).";
    public const string SerialNumber = "The serial number that names the page's logical stream, chosen at random and unique within the file (RFC 3533, section 6).";
    public const string SequenceNumber = "The page's number within its logical stream, which counts up by one so readers notice lost pages (RFC 3533, section 6).";
    public const string Crc = "The CRC-32 of the whole page, computed with this field set to 0 and the generator polynomial 0x04C11DB7 (RFC 3533, section 6).";
    public const string ComputedCrc = "The CRC-32 the analyzer computes over the page as the file holds it, to compare with the stored one.";
    public const string Segments = "How many lacing values the segment table holds, 0 to 255 (RFC 3533, section 6).";
    public const string SegmentTable = "The lacing values: the size of each segment of the page's body, in order (RFC 3533, section 5).";
    public const string LacingFull = "255: a full segment, after which the packet goes on in the next segment, on this page or the next (RFC 3533, section 5).";
    public const string LacingLast = "A segment shorter than 255 bytes, which ends its packet; 0 ends a packet whose length is a multiple of 255 (RFC 3533, section 5).";
    public const string LostBody = "The page's body, not read as packets because the page fails its CRC check or the file cuts it short.";
    public const string PacketData = "Bytes of one packet on this page: the segments the lacing values join, which may continue on the stream's next page (RFC 3533, section 5).";
    public const string OrphanData = "The rest of a packet whose start was on a page that is lost or missing; readers drop it.";
    public const string TooLargeData = "Part of a packet larger than the analyzer reads, so it is not handed to the codec.";

    public const string LogicalStream = "A logical stream: the pages of one serial number, which carry the packets of one codec (RFC 3533, section 4).";
    public const string StreamSerialNumber = "The serial number on every page of the stream (RFC 3533, section 6).";
    public const string Codec = "The codec the stream's first packet names by its first bytes, such as \"Speex\" and three spaces or 0x01 and \"vorbis\" (RFC 3533, section 4).";
    public const string CodecUnknown = "No known codec: the stream's first packet starts with bytes no Ogg mapping is known for, or is missing.";
    public const string ChainLink = "Which link of a chained file the stream belongs to, counting from 1: chaining puts complete groups of streams one after another (RFC 3533, section 4).";
    public const string PageCount = "How many pages of the stream the file holds.";
    public const string PacketCount = "How many complete packets the stream's pages hold.";
    public const string LastGranulePosition = "The granule position of the stream's last page that has one, which gives the stream's length in the codec's units.";
    public const string HasEndOfStream = "Whether a page of the stream carries the end-of-stream flag, as RFC 3533 asks of the last page of every logical stream (section 4).";

    public static string Lacing(int value) => value == 255 ? LacingFull : LacingLast;
}

namespace Encspect.Tags.VorbisComment;

// The background articles the Vorbis comment nodes point to, by the IDs the host's topic library
// serves.
internal static class Topics
{
    public const string VorbisComments = "vorbis-comments";
    public const string ReplayGain = "replaygain";
}

// What each Vorbis comment node is. Constants only, shared by every node of their kind; the
// structure from the Vorbis I specification, section 5.2, and the field names from its proposed
// list (section 5.2.2), with a few names in common use marked as such.
internal static class Explain
{
    public const string Tag = "A Vorbis comment: a vendor string and a list of NAME=value comments in UTF-8, the tag Vorbis, Opus, Speex and FLAC share (Vorbis I specification, section 5).";
    public const string VendorLength = "The length of the vendor string in bytes, a 32-bit little-endian integer (Vorbis I specification, section 5.2.1).";
    public const string Vendor = "The vendor string, which names the library or program that wrote the stream (Vorbis I specification, section 5.2.1).";
    public const string CommentCount = "How many comments follow, a 32-bit little-endian integer (Vorbis I specification, section 5.2.1).";
    public const string Length = "The length of the comment in bytes, a 32-bit little-endian integer (Vorbis I specification, section 5.2.1).";
    public const string FieldName = "The field name before the '=': ASCII 0x20 to 0x7D without '=', compared without regard to case (Vorbis I specification, section 5.2.2).";
    public const string Value = "The field's value after the '=', UTF-8 text to the end of the comment (Vorbis I specification, section 5.2.2).";
    public const string Text = "The comment's text, which has no '=' to split it into a field name and a value.";
    public const string Unparsed = "Bytes at the end of the data too few to hold a 4-byte length.";
    public const string NoSeparator = "A comment without '=', which therefore names no field (Vorbis I specification, section 5.2.2).";
    public const string InvalidName = "A comment whose field name is empty or holds characters outside ASCII 0x20 to 0x7D (Vorbis I specification, section 5.2.2).";
    public const string OtherField = "A comment under a field name outside the specification's proposed list; any name is allowed (Vorbis I specification, section 5.2.2).";

    private static readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TITLE"] = "The track or work name.",
        ["VERSION"] = "Which version of the track this is, such as a remix, to tell versions of one title apart.",
        ["ALBUM"] = "The collection the track belongs to.",
        ["TRACKNUMBER"] = "The track's number in its collection or album.",
        ["ARTIST"] = "The artist considered responsible for the work: the band or singer, the composer in classical music, the author of an audio book.",
        ["PERFORMER"] = "The artists who performed the work, such as the conductor, orchestra or soloists, or the reader of an audio book.",
        ["COPYRIGHT"] = "The copyright attribution.",
        ["LICENSE"] = "The license the track is under, such as a URL of a Creative Commons license.",
        ["ORGANIZATION"] = "The organization that produced the track, such as the record label.",
        ["DESCRIPTION"] = "A short description of the contents.",
        ["GENRE"] = "The music genre.",
        ["DATE"] = "The date the track was recorded.",
        ["LOCATION"] = "Where the track was recorded.",
        ["CONTACT"] = "How to reach the creators or distributors, such as a URL or an email address.",
        ["ISRC"] = "The track's International Standard Recording Code.",
        ["ENCODER"] = "The software that encoded the stream. Common practice, not in the specification's list.",
        ["ALBUMARTIST"] = "The artist of the album as a whole. Common practice, not in the specification's list.",
        ["TRACKTOTAL"] = "How many tracks the album has. Common practice, not in the specification's list.",
        ["DISCNUMBER"] = "The disc number of a release on several discs. Common practice, not in the specification's list.",
        ["COMPOSER"] = "The composer. Common practice, not in the specification's list.",
        ["COMMENT"] = "A free comment. Common practice, not in the specification's list.",
        ["REPLAYGAIN_TRACK_GAIN"] = "The ReplayGain track gain: the level change in dB that plays this track at the reference loudness. Common practice, not in the specification's list.",
        ["REPLAYGAIN_TRACK_PEAK"] = "The track's largest sample value, as a fraction of full scale, for ReplayGain's clipping prevention. Common practice, not in the specification's list.",
        ["REPLAYGAIN_ALBUM_GAIN"] = "The ReplayGain album gain: the level change in dB that plays the album at the reference loudness. Common practice, not in the specification's list.",
        ["REPLAYGAIN_ALBUM_PEAK"] = "The album's largest sample value, as a fraction of full scale. Common practice, not in the specification's list.",
    };

    public static string Field(string name) => Fields.GetValueOrDefault(name) ?? OtherField;

    public static string? FieldTopic(string name) =>
        name.StartsWith("REPLAYGAIN_", StringComparison.OrdinalIgnoreCase) ? Topics.ReplayGain : null;
}

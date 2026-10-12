namespace Encspect.Tags.Lyrics3;

// The background articles the Lyrics3 nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Lyrics3 = "lyrics3";
}

// What each Lyrics3 node is. Constants only, shared by every node of their kind.
internal static class Explain
{
    public const string V1Tag = "A Lyrics3 v1 tag, directly before the ID3v1 tag: lyrics of up to 5100 characters between LYRICSBEGIN and LYRICSEND (id3.org, Lyrics3).";
    public const string V2Tag = "A Lyrics3 v2.00 tag, directly before the ID3v1 tag: fields with a three-letter ID and a size, then the tag's size and LYRICS200 (id3.org, Lyrics3 v2.00).";
    public const string Begin = "The marker LYRICSBEGIN that starts the tag.";
    public const string Lyrics = "The lyrics as plain text, up to 5100 characters.";
    public const string V1End = "The marker LYRICSEND that ends a Lyrics3 v1 tag.";
    public const string V2End = "The marker LYRICS200 that ends a Lyrics3 v2.00 tag, which readers look for before the ID3v1 tag.";
    public const string V2Size = "The size of the tag from LYRICSBEGIN up to this field, in six decimal digits.";
    public const string Unparsed = "Bytes of the tag the analyzer could not read as fields.";
    public const string FieldId = "The field's three-letter ID.";
    public const string FieldSize = "The size of the field's data, in five decimal digits.";
    public const string FieldData = "The field's data, characters 01 to 254.";
    public const string HasLyrics = "Whether the tag holds a lyrics field.";
    public const string HasTimestamps = "Whether the lyrics carry timestamps.";
    public const string IsRandomSelectionInhibited = "Whether players should leave the track out of a random selection.";
    public const string Line = "One line of the lyrics.";
    public const string LineTimestamp = "When this line is sung, as [mm:ss] from the start of the track.";
    public const string Image = "One image link: a file name, an optional description and an optional timestamp, separated by ||.";
    public const string ImageFilename = "The image file's name.";
    public const string ImageDescription = "A description of the image.";
    public const string ImageTimestamp = "When the image should be shown, as [mm:ss] from the start of the track.";
    public const string UnknownField = "A field that Lyrics3 v2.00 does not define.";

    public static string Field(string id) => id switch
    {
        "IND" => "Indications: whether the tag holds lyrics, whether they carry timestamps, and whether players should leave the track out of a random selection.",
        "LYR" => "The lyrics, lines separated by CR LF, with optional [mm:ss] timestamps.",
        "INF" => "Additional information, as free text.",
        "AUT" => "The author of the lyrics.",
        "EAL" => "The album name in full, for one longer than the 30 characters of the ID3v1 field.",
        "EAR" => "The artist name in full, for one longer than the 30 characters of the ID3v1 field.",
        "ETT" => "The track title in full, for one longer than the 30 characters of the ID3v1 field.",
        "IMG" => "Links to images, one per line: a file name, an optional description and an optional timestamp.",
        _ => UnknownField,
    };
}

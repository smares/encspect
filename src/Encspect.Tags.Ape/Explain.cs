namespace Encspect.Tags.Ape;

// The background articles the APE nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string ApeTag = "ape-tag";
    public const string ReplayGain = "replaygain";
}

// What each APE node is. Constants only, shared by every node of their kind; item keys from the
// APEv2 specification's list of standard keys (wiki.hydrogenaud.io, "APE key") and the keys
// ReplayGain and MP3Gain write.
internal static class Explain
{
    public const string Tag = "An APE tag: items of a key and a value between an optional header and a footer of 32 bytes each (APEv1 and APEv2 specifications).";
    public const string Header = "The 32-byte header, which repeats the footer so the tag can also be read from its start.";
    public const string Footer = "The 32-byte footer, which readers find at the end of the file to read the tag from there.";
    public const string Preamble = "The signature APETAGEX.";
    public const string Version = "The tag's version: 1000 for APEv1, 2000 for APEv2.";
    public const string TagSize = "The size of the items and the footer in bytes, without the header.";
    public const string ItemCount = "How many items the tag holds.";
    public const string Flags = "The tag's flags, which only APEv2 defines.";
    public const string HasHeader = "Whether the tag has a header.";
    public const string HasFooter = "Whether the tag has a footer; the stored bit is set when it has none.";
    public const string IsHeader = "Whether this block is the header rather than the footer, the one bit in which the two differ.";
    public const string IsReadOnly = "Whether the tag is meant not to be changed.";
    public const string Reserved = "Eight bytes that must be zero.";
    public const string Items = "The tag's items, each a key with its value.";
    public const string Unparsed = "Bytes the analyzer could not read as items or a footer.";
    public const string ItemSize = "The size of the item's value in bytes.";
    public const string ItemFlags = "The item's flags: whether it is read-only, and the type of its value.";
    public const string ItemReadOnly = "Whether the item is meant not to be changed.";
    public const string ItemType = "What the value holds: UTF-8 text, binary data, or a link to external data.";
    public const string Key = "The item's key, 2 to 255 ASCII characters, compared without regard to case.";
    public const string Value = "The item's value. A text value may hold several values, separated by zero bytes.";
    public const string BinaryValue = "The item's binary value, such as a picture.";
    public const string UnknownItem = "An item under a key that is not among the standard ones; APE keys are free text.";

    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Title"] = "The title of the piece.",
        ["Subtitle"] = "A subtitle, where the title holds the work or the subtitle refines it.",
        ["Artist"] = "The performing artist.",
        ["Album"] = "The album.",
        ["Debut Album"] = "The album the piece first appeared on.",
        ["Publisher"] = "The publisher or record label.",
        ["Conductor"] = "The conductor.",
        ["Track"] = "The track number.",
        ["Composer"] = "The composer.",
        ["Comment"] = "A comment.",
        ["Copyright"] = "The copyright holder.",
        ["Publicationright"] = "The holder of the publication right.",
        ["File"] = "Where the file can be found.",
        ["EAN/UPC"] = "The EAN-13 or UPC-A bar code of the release.",
        ["ISBN"] = "The ISBN, with its check digit.",
        ["Catalog"] = "The catalog number.",
        ["LC"] = "The label code.",
        ["Year"] = "The release date.",
        ["Record Date"] = "The date of the recording.",
        ["Record Location"] = "Where the recording was made.",
        ["Genre"] = "The genre.",
        ["Media"] = "The source media, such as a CD.",
        ["Index"] = "Index times within the piece.",
        ["Related"] = "Where related information can be found.",
        ["ISRC"] = "The International Standard Recording Code.",
        ["Abstract"] = "An abstract of the content.",
        ["Language"] = "The languages of the music or speech.",
        ["Bibliography"] = "A bibliography or discography.",
        ["Introplay"] = "A characteristic part of the piece, for playing an intro.",
        ["Dummy"] = "A placeholder.",
        ["Album Artist"] = "The artist of the album as a whole. Common practice, not in the APEv2 list of keys.",
        ["Disc"] = "The disc number. Common practice, not in the APEv2 list of keys.",
        ["Cover Art (Front)"] = "The front cover, as a file name, a zero byte and the image. Common practice, not in the APEv2 list of keys.",
        ["Cover Art (Back)"] = "The back cover, as a file name, a zero byte and the image. Common practice, not in the APEv2 list of keys.",
        ["REPLAYGAIN_TRACK_GAIN"] = "The ReplayGain track gain: the level change in dB that plays this track at the reference loudness.",
        ["REPLAYGAIN_TRACK_PEAK"] = "The track's largest sample value, as a fraction of full scale, for ReplayGain's clipping prevention.",
        ["REPLAYGAIN_ALBUM_GAIN"] = "The ReplayGain album gain: the level change in dB that plays the album at the reference loudness.",
        ["REPLAYGAIN_ALBUM_PEAK"] = "The album's largest sample value, as a fraction of full scale.",
        ["MP3GAIN_MINMAX"] = "The smallest and largest global_gain of the frames, as MP3Gain records them.",
        ["MP3GAIN_ALBUM_MINMAX"] = "The smallest and largest global_gain of the album's frames, as MP3Gain records them.",
        ["MP3GAIN_UNDO"] = "The change MP3Gain made to the frames' global_gain, which lets it undo the change.",
    };

    public static string Item(string key) => Keys.GetValueOrDefault(key) ?? UnknownItem;

    public static string? ItemTopic(string key) =>
        key.StartsWith("REPLAYGAIN_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("MP3GAIN_", StringComparison.OrdinalIgnoreCase) ? Topics.ReplayGain : null;
}

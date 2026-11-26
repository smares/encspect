namespace Encspect.Tags.Id3;

// What each ID3v2 frame holds, by frame ID, worded for the version of the tag the frame is in and
// citing that version's own specification: ID3v2.2 section 4 (three-letter IDs), ID3v2.3.0
// section 4 and the ID3v2.4.0 frames document's section 4. Each frame is listed with the section
// that defines it in ID3v2.3.0 and in ID3v2.4.0 (null where that version has no such frame), taken
// from the documents' own headings (scripts/fixtures/id3_specs.py fetches them). Built once; a
// frame node takes its string from here.
internal static class Id3v2FrameTexts
{
    private const string Unknown = "A frame this analyzer has no description for, such as one a program defined for itself; its content is shown as its byte count.";
    private const string ExperimentalV22 = "An experimental frame: IDs starting with X, Y or Z are free for experiments (ID3v2.2 section 3.2).";
    private const string ExperimentalV23 = "An experimental frame: IDs starting with X, Y or Z are free for experiments (ID3v2.3.0 section 3.3).";
    private const string ExperimentalV24 = "An experimental frame: IDs starting with X, Y or Z are free for experiments (ID3v2.4.0 structure section 4).";

    // gone: what ID3v2.4 did with a frame it does not define, worded after "ID3v2.4 ".
    private readonly record struct FrameDefinition(string Body, string? V23, string? V24, string? Gone = null);

    private static readonly Dictionary<string, FrameDefinition> Definitions = new(StringComparer.Ordinal)
    {
        ["AENC"] = new("Audio encryption: who encrypted the audio, and a preview stretch left unencrypted", "4.20", "4.19"),
        ["APIC"] = new("Attached picture: an image such as the cover, with its type and a description", "4.15", "4.14"),
        ["ASPI"] = new("Audio seek point index: a table of positions in the audio for seeking", null, "4.30"),
        ["COMM"] = new("Comment: free text with a language and a short description", "4.11", "4.10"),
        ["COMR"] = new("Commercial frame: an offer to buy the recording, with price, seller and delivery", "4.25", "4.24"),
        ["ENCR"] = new("Encryption method registration: links a method number used by encrypted frames to its owner", "4.26", "4.25"),
        ["EQU2"] = new("Equalisation (2): level adjustments by frequency the file's creator suggests", null, "4.12"),
        ["EQUA"] = new("Equalization: level adjustments by frequency the file's creator suggests", "4.13", null, "replaced it with EQU2"),
        ["ETCO"] = new("Event timing codes: points in the audio such as the start of the verse or the end of the intro", "4.6", "4.5"),
        ["GEOB"] = new("General encapsulated object: any file embedded in the tag, with its media type, name and description", "4.16", "4.15"),
        ["GRID"] = new("Group identification registration: links a group number used by frames to its owner", "4.27", "4.26"),
        ["IPLS"] = new("Involved people list: people and their roles in the recording", "4.4", null, "replaced it with TIPL and TMCL"),
        ["LINK"] = new("Linked information: a frame kept in another file, referred to by its ID and URL", "4.21", "4.20"),
        ["MCDI"] = new("Music CD identifier: the table of contents of the CD the audio was taken from", "4.5", "4.4"),
        ["MLLT"] = new("MPEG location lookup table: frame and time positions for seeking", "4.7", "4.6"),
        ["OWNE"] = new("Ownership frame: the price paid, the date of purchase and the seller", "4.24", "4.23"),
        ["PCNT"] = new("Play counter: how many times the file was played", "4.17", "4.16"),
        ["POPM"] = new("Popularimeter: a user's rating of the track and a play count, by email address", "4.18", "4.17"),
        ["POSS"] = new("Position synchronisation: where in the audio the file starts, for a stream joined midway", "4.22", "4.21"),
        ["PRIV"] = new("Private frame: data in a format its owner defines, such as a program's own settings", "4.28", "4.27"),
        ["RBUF"] = new("Recommended buffer size: how much a player should buffer when the file is streamed", "4.19", "4.18"),
        ["RVA2"] = new("Relative volume adjustment (2): level changes by channel, such as a gain to apply on playback", null, "4.11"),
        ["RVAD"] = new("Relative volume adjustment: level changes by channel", "4.12", null, "replaced it with RVA2"),
        ["RVRB"] = new("Reverb: reverb settings the file's creator suggests", "4.14", "4.13"),
        ["SEEK"] = new("Seek frame: the minimum distance from the end of this tag to the next tag in the file", null, "4.29"),
        ["SIGN"] = new("Signature frame: a signature over a group of frames", null, "4.28"),
        ["SYLT"] = new("Synchronised lyrics or text: text with the time each part is sung or shown", "4.10", "4.9"),
        ["SYTC"] = new("Synchronised tempo codes: the tempo over the course of the audio", "4.8", "4.7"),
        ["TALB"] = new("Album, movie or show title", "4.2.1", "4.2.1"),
        ["TBPM"] = new("Beats per minute of the music", "4.2.1", "4.2.3"),
        ["TCOM"] = new("Composer", "4.2.1", "4.2.2"),
        ["TCON"] = new("Content type, the genre: free text or ID3v1 genre numbers", "4.2.1", "4.2.3"),
        ["TCOP"] = new("Copyright message, starting with the year", "4.2.1", "4.2.4"),
        ["TDAT"] = new("Date of the recording as DDMM", "4.2.1", null, "replaced it with TDRC"),
        ["TDEN"] = new("Encoding time: when the audio was encoded", null, "4.2.5"),
        ["TDLY"] = new("Playlist delay: milliseconds of silence to insert between this track and the previous one", "4.2.1", "4.2.5"),
        ["TDOR"] = new("Original release time, for a recording that was released before", null, "4.2.5"),
        ["TDRC"] = new("Recording time, the date ID3v2.4 uses for the year", null, "4.2.5"),
        ["TDRL"] = new("Release time: when the recording was first released", null, "4.2.5"),
        ["TDTG"] = new("Tagging time: when the tag was written", null, "4.2.5"),
        ["TENC"] = new("Encoded by: the person or organization that encoded the file", "4.2.1", "4.2.2"),
        ["TEXT"] = new("Lyricist or text writer", "4.2.1", "4.2.2"),
        ["TFLT"] = new("File type, such as MPG/3 for MPEG Layer III", "4.2.1", "4.2.3"),
        ["TIME"] = new("Time of the recording as HHMM", "4.2.1", null, "replaced it with TDRC"),
        ["TIPL"] = new("Involved people list: people and their roles, such as producer or engineer", null, "4.2.2"),
        ["TIT1"] = new("Content group description, such as a classical work the track belongs to", "4.2.1", "4.2.1"),
        ["TIT2"] = new("Title of the track or piece", "4.2.1", "4.2.1"),
        ["TIT3"] = new("Subtitle or description refinement, such as a movement or a version", "4.2.1", "4.2.1"),
        ["TKEY"] = new("Initial musical key, such as Cm", "4.2.1", "4.2.3"),
        ["TLAN"] = new("Languages of the lyrics or speech, as ISO 639-2 codes", "4.2.1", "4.2.3"),
        ["TLEN"] = new("Length of the audio in milliseconds, as the tagger states it", "4.2.1", "4.2.3"),
        ["TMCL"] = new("Musician credits list: musicians and their instruments", null, "4.2.2"),
        ["TMED"] = new("Media type the audio came from, such as a CD or vinyl", "4.2.1", "4.2.3"),
        ["TMOO"] = new("Mood of the music", null, "4.2.3"),
        ["TOAL"] = new("Original album, movie or show title, for a cover or a re-release", "4.2.1", "4.2.1"),
        ["TOFN"] = new("Original filename", "4.2.1", "4.2.5"),
        ["TOLY"] = new("Original lyricist or text writer", "4.2.1", "4.2.2"),
        ["TOPE"] = new("Original artist or performer, for a cover version", "4.2.1", "4.2.2"),
        ["TORY"] = new("Original release year", "4.2.1", null, "replaced it with TDOR"),
        ["TOWN"] = new("File owner or licensee", "4.2.1", "4.2.4"),
        ["TPE1"] = new("Lead artist, performer or soloist", "4.2.1", "4.2.2"),
        ["TPE2"] = new("Band, orchestra or accompaniment; many programs use it for the album artist", "4.2.1", "4.2.2"),
        ["TPE3"] = new("Conductor", "4.2.1", "4.2.2"),
        ["TPE4"] = new("Interpreted, remixed or otherwise modified by", "4.2.1", "4.2.2"),
        ["TPOS"] = new("Part of a set: the disc number, optionally with the total, such as 1/2", "4.2.1", "4.2.1"),
        ["TPRO"] = new("Produced notice: the production copyright, starting with the year", null, "4.2.4"),
        ["TPUB"] = new("Publisher or label", "4.2.1", "4.2.4"),
        ["TRCK"] = new("Track number, optionally with the total, such as 3/12", "4.2.1", "4.2.1"),
        ["TRDA"] = new("Recording dates as free text", "4.2.1", null, "replaced it with TDRC"),
        ["TRSN"] = new("Internet radio station name", "4.2.1", "4.2.4"),
        ["TRSO"] = new("Internet radio station owner", "4.2.1", "4.2.4"),
        ["TSIZ"] = new("Size of the audio in bytes, without the tag", "4.2.1", null, "dropped it"),
        ["TSOA"] = new("Album sort order: the album title as lists should sort it", null, "4.2.5"),
        ["TSOP"] = new("Performer sort order: the artist as lists should sort it", null, "4.2.5"),
        ["TSOT"] = new("Title sort order: the title as lists should sort it", null, "4.2.5"),
        ["TSRC"] = new("ISRC, the International Standard Recording Code of the recording", "4.2.1", "4.2.1"),
        ["TSSE"] = new("Software, hardware and settings used for encoding, as the program that wrote the tag fills it in", "4.2.1", "4.2.5"),
        ["TSST"] = new("Set subtitle: the title of the disc this track is on", null, "4.2.1"),
        ["TXXX"] = new("User-defined text: a value under a description of its own, such as a ReplayGain figure or a MusicBrainz ID", "4.2.2", "4.2.6"),
        ["TYER"] = new("Year of the recording", "4.2.1", null, "replaced it with TDRC"),
        ["UFID"] = new("Unique file identifier: an ID an organization or database gave the file", "4.1", "4.1"),
        ["USER"] = new("Terms of use, in a given language", "4.23", "4.22"),
        ["USLT"] = new("Unsynchronised lyrics or text transcription, with a language and a description", "4.9", "4.8"),
        ["WCOM"] = new("Commercial information: a web page about buying the recording", "4.3.1", "4.3.1"),
        ["WCOP"] = new("Copyright or legal information web page", "4.3.1", "4.3.1"),
        ["WOAF"] = new("Official web page of the audio file", "4.3.1", "4.3.1"),
        ["WOAR"] = new("Official web page of the artist or performer", "4.3.1", "4.3.1"),
        ["WOAS"] = new("Official web page of the audio source, such as the movie", "4.3.1", "4.3.1"),
        ["WORS"] = new("Official home page of the internet radio station", "4.3.1", "4.3.1"),
        ["WPAY"] = new("Payment: a web page for paying for the file", "4.3.1", "4.3.1"),
        ["WPUB"] = new("Official web page of the publisher", "4.3.1", "4.3.1"),
        ["WXXX"] = new("User-defined URL: a link under a description of its own", "4.3.2", "4.3.2"),
    };

    // Frames programs wrote that no ID3v2 version defines.
    private static readonly Dictionary<string, string> Unspecified = new(StringComparer.Ordinal)
    {
        ["TCMP"] = "Part of a compilation, 1 when it is. iTunes writes it; no ID3v2 version defines it.",
        ["TSO2"] = "Album artist sort order. iTunes writes it; no ID3v2 version defines it.",
        ["TSOC"] = "Composer sort order. iTunes writes it; no ID3v2 version defines it.",
        ["GRP1"] = "Grouping. iTunes writes it; no ID3v2 version defines it.",
        ["MVNM"] = "Movement name. iTunes writes it; no ID3v2 version defines it.",
        ["MVIN"] = "Movement number. iTunes writes it; no ID3v2 version defines it.",
    };

    private static readonly Dictionary<string, string> V23Frames = Definitions.ToDictionary(
        p => p.Key,
        p => p.Value.V23 is { } section
            ? $"{p.Value.Body}{Gone(p.Value)} (ID3v2.3.0 section {section})."
            : $"{p.Value.Body} (ID3v2.4.0 frames section {p.Value.V24}; not part of ID3v2.3).",
        StringComparer.Ordinal);

    private static readonly Dictionary<string, string> V24Frames = Definitions.ToDictionary(
        p => p.Key,
        p => p.Value.V24 is { } section
            ? $"{p.Value.Body} (ID3v2.4.0 frames section {section})."
            : $"{p.Value.Body} (ID3v2.3.0 section {p.Value.V23}; not part of ID3v2.4, which {p.Value.Gone}).",
        StringComparer.Ordinal);

    // ID3v2.2 section 4 names the same frames with three letters, each in the section given here.
    private static readonly Dictionary<string, (string Frame, string Section)> V22Ids = new(StringComparer.Ordinal)
    {
        ["BUF"] = ("RBUF", "4.19"),
        ["CNT"] = ("PCNT", "4.17"),
        ["COM"] = ("COMM", "4.11"),
        ["CRA"] = ("AENC", "4.21"),
        ["ETC"] = ("ETCO", "4.6"),
        ["EQU"] = ("EQUA", "4.13"),
        ["GEO"] = ("GEOB", "4.16"),
        ["IPL"] = ("IPLS", "4.4"),
        ["LNK"] = ("LINK", "4.22"),
        ["MCI"] = ("MCDI", "4.5"),
        ["MLL"] = ("MLLT", "4.7"),
        ["PIC"] = ("APIC", "4.15"),
        ["POP"] = ("POPM", "4.18"),
        ["REV"] = ("RVRB", "4.14"),
        ["RVA"] = ("RVAD", "4.12"),
        ["SLT"] = ("SYLT", "4.10"),
        ["STC"] = ("SYTC", "4.8"),
        ["TAL"] = ("TALB", "4.2.1"),
        ["TBP"] = ("TBPM", "4.2.1"),
        ["TCM"] = ("TCOM", "4.2.1"),
        ["TCO"] = ("TCON", "4.2.1"),
        ["TCR"] = ("TCOP", "4.2.1"),
        ["TDA"] = ("TDAT", "4.2.1"),
        ["TDY"] = ("TDLY", "4.2.1"),
        ["TEN"] = ("TENC", "4.2.1"),
        ["TFT"] = ("TFLT", "4.2.1"),
        ["TIM"] = ("TIME", "4.2.1"),
        ["TKE"] = ("TKEY", "4.2.1"),
        ["TLA"] = ("TLAN", "4.2.1"),
        ["TLE"] = ("TLEN", "4.2.1"),
        ["TMT"] = ("TMED", "4.2.1"),
        ["TOA"] = ("TOPE", "4.2.1"),
        ["TOF"] = ("TOFN", "4.2.1"),
        ["TOL"] = ("TOLY", "4.2.1"),
        ["TOR"] = ("TORY", "4.2.1"),
        ["TOT"] = ("TOAL", "4.2.1"),
        ["TP1"] = ("TPE1", "4.2.1"),
        ["TP2"] = ("TPE2", "4.2.1"),
        ["TP3"] = ("TPE3", "4.2.1"),
        ["TP4"] = ("TPE4", "4.2.1"),
        ["TPA"] = ("TPOS", "4.2.1"),
        ["TPB"] = ("TPUB", "4.2.1"),
        ["TRC"] = ("TSRC", "4.2.1"),
        ["TRD"] = ("TRDA", "4.2.1"),
        ["TRK"] = ("TRCK", "4.2.1"),
        ["TSI"] = ("TSIZ", "4.2.1"),
        ["TSS"] = ("TSSE", "4.2.1"),
        ["TT1"] = ("TIT1", "4.2.1"),
        ["TT2"] = ("TIT2", "4.2.1"),
        ["TT3"] = ("TIT3", "4.2.1"),
        ["TXT"] = ("TEXT", "4.2.1"),
        ["TXX"] = ("TXXX", "4.2.2"),
        ["TYE"] = ("TYER", "4.2.1"),
        ["UFI"] = ("UFID", "4.1"),
        ["ULT"] = ("USLT", "4.9"),
        ["WAF"] = ("WOAF", "4.3.1"),
        ["WAR"] = ("WOAR", "4.3.1"),
        ["WAS"] = ("WOAS", "4.3.1"),
        ["WCM"] = ("WCOM", "4.3.1"),
        ["WCP"] = ("WCOP", "4.3.1"),
        ["WPB"] = ("WPUB", "4.3.1"),
        ["WXX"] = ("WXXX", "4.3.2"),
    };

    private static readonly Dictionary<string, string> V22Frames = V22Ids.ToDictionary(
        p => p.Key,
        p => $"The ID3v2.2 form of {p.Value.Frame}. {Definitions[p.Value.Frame].Body}{Gone(Definitions[p.Value.Frame])} (ID3v2.2 section {p.Value.Section}).",
        StringComparer.Ordinal);

    private const string EncryptedMeta = "Encrypted meta frame: other frames, encrypted together (ID3v2.2 section 4.20).";

    public static string For(int major, string id)
    {
        bool isExperimental = id[0] is 'X' or 'Y' or 'Z';
        return major switch
        {
            2 => id == "CRM" ? EncryptedMeta : V22Frames.GetValueOrDefault(id) ?? (isExperimental ? ExperimentalV22 : Unknown),
            3 => V23Frames.GetValueOrDefault(id) ?? Unspecified.GetValueOrDefault(id) ?? (isExperimental ? ExperimentalV23 : Unknown),
            _ => V24Frames.GetValueOrDefault(id) ?? Unspecified.GetValueOrDefault(id) ?? (isExperimental ? ExperimentalV24 : Unknown),
        };
    }

    private static string Gone(FrameDefinition frame) => frame.Gone is { } gone ? $"; ID3v2.4 {gone}" : string.Empty;

    // The topic of a frame whose content has one beyond the tag's.
    public static string? TopicFor(int major, string id) => (major == 2 && V22Ids.TryGetValue(id, out (string Frame, string Section) v23) ? v23.Frame : id) switch
    {
        "TSSE" => Topics.EncoderDetection,
        _ => null,
    };
}

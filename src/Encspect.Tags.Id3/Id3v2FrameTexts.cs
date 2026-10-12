namespace Encspect.Tags.Id3;

// What each ID3v2 frame holds, by frame ID: ID3v2.3 and 2.4 (ID3v2.4.0 frames section 4, ID3v2.3.0
// section 4) and the three-letter IDs of ID3v2.2 (ID3v2.2 section 4), which name the same frames.
// Built once; a frame node takes its string from here.
internal static class Id3v2FrameTexts
{
    private const string Unknown = "A frame this analyzer has no description for, such as one a program defined for itself; its content is shown as its byte count.";
    private const string Experimental = "An experimental frame: IDs starting with X, Y or Z are free for experiments (ID3v2.4.0 section 4).";

    private static readonly Dictionary<string, string> Frames = new(StringComparer.Ordinal)
    {
        ["AENC"] = "Audio encryption: who encrypted the audio, and a preview stretch left unencrypted (ID3v2.4.0 frames section 4.19).",
        ["APIC"] = "Attached picture: an image such as the cover, with its type and a description (ID3v2.4.0 frames section 4.14).",
        ["ASPI"] = "Audio seek point index: a table of positions in the audio for seeking (ID3v2.4.0 frames section 4.30).",
        ["COMM"] = "Comment: free text with a language and a short description (ID3v2.4.0 frames section 4.10).",
        ["COMR"] = "Commercial frame: an offer to buy the recording, with price, seller and delivery (ID3v2.4.0 frames section 4.24).",
        ["ENCR"] = "Encryption method registration: links a method number used by encrypted frames to its owner (ID3v2.4.0 frames section 4.25).",
        ["EQU2"] = "Equalisation (2): level adjustments by frequency the file's creator suggests (ID3v2.4.0 frames section 4.12).",
        ["EQUA"] = "Equalization: level adjustments by frequency the file's creator suggests; ID3v2.4 replaced it with EQU2 (ID3v2.3.0 section 4.13).",
        ["ETCO"] = "Event timing codes: points in the audio such as the start of the verse or the end of the intro (ID3v2.4.0 frames section 4.5).",
        ["GEOB"] = "General encapsulated object: any file embedded in the tag, with its media type, name and description (ID3v2.4.0 frames section 4.15).",
        ["GRID"] = "Group identification registration: links a group number used by frames to its owner (ID3v2.4.0 frames section 4.26).",
        ["IPLS"] = "Involved people list: people and their roles in the recording; ID3v2.4 replaced it with TIPL and TMCL (ID3v2.3.0 section 4.4).",
        ["LINK"] = "Linked information: a frame kept in another file, referred to by its ID and URL (ID3v2.4.0 frames section 4.20).",
        ["MCDI"] = "Music CD identifier: the table of contents of the CD the audio was taken from (ID3v2.4.0 frames section 4.4).",
        ["MLLT"] = "MPEG location lookup table: frame and time positions for seeking (ID3v2.4.0 frames section 4.6).",
        ["OWNE"] = "Ownership frame: the price paid, the date of purchase and the seller (ID3v2.4.0 frames section 4.23).",
        ["PCNT"] = "Play counter: how many times the file was played (ID3v2.4.0 frames section 4.16).",
        ["POPM"] = "Popularimeter: a user's rating of the track and a play count, by email address (ID3v2.4.0 frames section 4.17).",
        ["POSS"] = "Position synchronisation: where in the audio the file starts, for a stream joined midway (ID3v2.4.0 frames section 4.21).",
        ["PRIV"] = "Private frame: data in a format its owner defines, such as a program's own settings (ID3v2.4.0 frames section 4.27).",
        ["RBUF"] = "Recommended buffer size: how much a player should buffer when the file is streamed (ID3v2.4.0 frames section 4.18).",
        ["RVA2"] = "Relative volume adjustment (2): level changes by channel, such as a gain to apply on playback (ID3v2.4.0 frames section 4.11).",
        ["RVAD"] = "Relative volume adjustment: level changes by channel; ID3v2.4 replaced it with RVA2 (ID3v2.3.0 section 4.12).",
        ["RVRB"] = "Reverb: reverb settings the file's creator suggests (ID3v2.4.0 frames section 4.13).",
        ["SEEK"] = "Seek frame: the minimum distance from the end of this tag to the next tag in the file (ID3v2.4.0 frames section 4.29).",
        ["SIGN"] = "Signature frame: a signature over a group of frames (ID3v2.4.0 frames section 4.28).",
        ["SYLT"] = "Synchronised lyrics or text: text with the time each part is sung or shown (ID3v2.4.0 frames section 4.9).",
        ["SYTC"] = "Synchronised tempo codes: the tempo over the course of the audio (ID3v2.4.0 frames section 4.7).",
        ["TALB"] = "Album, movie or show title (ID3v2.4.0 frames section 4.2.1).",
        ["TBPM"] = "Beats per minute of the music (ID3v2.4.0 frames section 4.2.3).",
        ["TCOM"] = "Composer (ID3v2.4.0 frames section 4.2.2).",
        ["TCON"] = "Content type, the genre: free text or ID3v1 genre numbers (ID3v2.4.0 frames section 4.2.3).",
        ["TCOP"] = "Copyright message, starting with the year (ID3v2.4.0 frames section 4.2.4).",
        ["TDAT"] = "Date of the recording as DDMM; ID3v2.4 replaced it with TDRC (ID3v2.3.0 section 4.2.1).",
        ["TDEN"] = "Encoding time: when the audio was encoded (ID3v2.4.0 frames section 4.2.5).",
        ["TDLY"] = "Playlist delay: milliseconds of silence to insert between this track and the previous one (ID3v2.4.0 frames section 4.2.5).",
        ["TDOR"] = "Original release time, for a recording that was released before (ID3v2.4.0 frames section 4.2.5).",
        ["TDRC"] = "Recording time, the date ID3v2.4 uses for the year (ID3v2.4.0 frames section 4.2.5).",
        ["TDRL"] = "Release time: when the recording was first released (ID3v2.4.0 frames section 4.2.5).",
        ["TDTG"] = "Tagging time: when the tag was written (ID3v2.4.0 frames section 4.2.5).",
        ["TENC"] = "Encoded by: the person or organization that encoded the file (ID3v2.4.0 frames section 4.2.2).",
        ["TEXT"] = "Lyricist or text writer (ID3v2.4.0 frames section 4.2.2).",
        ["TFLT"] = "File type, such as MPG/3 for MPEG Layer III (ID3v2.4.0 frames section 4.2.3).",
        ["TIME"] = "Time of the recording as HHMM; ID3v2.4 replaced it with TDRC (ID3v2.3.0 section 4.2.1).",
        ["TIPL"] = "Involved people list: people and their roles, such as producer or engineer (ID3v2.4.0 frames section 4.2.2).",
        ["TIT1"] = "Content group description, such as a classical work the track belongs to (ID3v2.4.0 frames section 4.2.1).",
        ["TIT2"] = "Title of the track or piece (ID3v2.4.0 frames section 4.2.1).",
        ["TIT3"] = "Subtitle or description refinement, such as a movement or a version (ID3v2.4.0 frames section 4.2.1).",
        ["TKEY"] = "Initial musical key, such as Cm (ID3v2.4.0 frames section 4.2.3).",
        ["TLAN"] = "Languages of the lyrics or speech, as ISO 639-2 codes (ID3v2.4.0 frames section 4.2.3).",
        ["TLEN"] = "Length of the audio in milliseconds, as the tagger states it (ID3v2.4.0 frames section 4.2.3).",
        ["TMCL"] = "Musician credits list: musicians and their instruments (ID3v2.4.0 frames section 4.2.2).",
        ["TMED"] = "Media type the audio came from, such as a CD or vinyl (ID3v2.4.0 frames section 4.2.3).",
        ["TMOO"] = "Mood of the music (ID3v2.4.0 frames section 4.2.3).",
        ["TOAL"] = "Original album, movie or show title, for a cover or a re-release (ID3v2.4.0 frames section 4.2.1).",
        ["TOFN"] = "Original filename (ID3v2.4.0 frames section 4.2.5).",
        ["TOLY"] = "Original lyricist or text writer (ID3v2.4.0 frames section 4.2.2).",
        ["TOPE"] = "Original artist or performer, for a cover version (ID3v2.4.0 frames section 4.2.2).",
        ["TORY"] = "Original release year; ID3v2.4 replaced it with TDOR (ID3v2.3.0 section 4.2.1).",
        ["TOWN"] = "File owner or licensee (ID3v2.4.0 frames section 4.2.4).",
        ["TPE1"] = "Lead artist, performer or soloist (ID3v2.4.0 frames section 4.2.2).",
        ["TPE2"] = "Band, orchestra or accompaniment; many programs use it for the album artist (ID3v2.4.0 frames section 4.2.2).",
        ["TPE3"] = "Conductor (ID3v2.4.0 frames section 4.2.2).",
        ["TPE4"] = "Interpreted, remixed or otherwise modified by (ID3v2.4.0 frames section 4.2.2).",
        ["TPOS"] = "Part of a set: the disc number, optionally with the total, such as 1/2 (ID3v2.4.0 frames section 4.2.1).",
        ["TPRO"] = "Produced notice: the production copyright, starting with the year (ID3v2.4.0 frames section 4.2.4).",
        ["TPUB"] = "Publisher or label (ID3v2.4.0 frames section 4.2.4).",
        ["TRCK"] = "Track number, optionally with the total, such as 3/12 (ID3v2.4.0 frames section 4.2.1).",
        ["TRDA"] = "Recording dates as free text; ID3v2.4 replaced it with TDRC (ID3v2.3.0 section 4.2.1).",
        ["TRSN"] = "Internet radio station name (ID3v2.4.0 frames section 4.2.4).",
        ["TRSO"] = "Internet radio station owner (ID3v2.4.0 frames section 4.2.4).",
        ["TSIZ"] = "Size of the audio in bytes, without the tag; ID3v2.4 dropped it (ID3v2.3.0 section 4.2.1).",
        ["TSOA"] = "Album sort order: the album title as lists should sort it (ID3v2.4.0 frames section 4.2.5).",
        ["TSOP"] = "Performer sort order: the artist as lists should sort it (ID3v2.4.0 frames section 4.2.5).",
        ["TSOT"] = "Title sort order: the title as lists should sort it (ID3v2.4.0 frames section 4.2.5).",
        ["TSRC"] = "ISRC, the International Standard Recording Code of the recording (ID3v2.4.0 frames section 4.2.1).",
        ["TSSE"] = "Software, hardware and settings used for encoding, as the program that wrote the tag fills it in (ID3v2.4.0 frames section 4.2.5).",
        ["TSST"] = "Set subtitle: the title of the disc this track is on (ID3v2.4.0 frames section 4.2.1).",
        ["TXXX"] = "User-defined text: a value under a description of its own, such as a ReplayGain figure or a MusicBrainz ID (ID3v2.4.0 frames section 4.2.6).",
        ["TYER"] = "Year of the recording; ID3v2.4 replaced it with TDRC (ID3v2.3.0 section 4.2.1).",
        ["UFID"] = "Unique file identifier: an ID an organization or database gave the file (ID3v2.4.0 frames section 4.1).",
        ["USER"] = "Terms of use, in a given language (ID3v2.4.0 frames section 4.22).",
        ["USLT"] = "Unsynchronised lyrics or text transcription, with a language and a description (ID3v2.4.0 frames section 4.8).",
        ["WCOM"] = "Commercial information: a web page about buying the recording (ID3v2.4.0 frames section 4.3.1).",
        ["WCOP"] = "Copyright or legal information web page (ID3v2.4.0 frames section 4.3.1).",
        ["WOAF"] = "Official web page of the audio file (ID3v2.4.0 frames section 4.3.1).",
        ["WOAR"] = "Official web page of the artist or performer (ID3v2.4.0 frames section 4.3.1).",
        ["WOAS"] = "Official web page of the audio source, such as the movie (ID3v2.4.0 frames section 4.3.1).",
        ["WORS"] = "Official home page of the internet radio station (ID3v2.4.0 frames section 4.3.1).",
        ["WPAY"] = "Payment: a web page for paying for the file (ID3v2.4.0 frames section 4.3.1).",
        ["WPUB"] = "Official web page of the publisher (ID3v2.4.0 frames section 4.3.1).",
        ["WXXX"] = "User-defined URL: a link under a description of its own (ID3v2.4.0 frames section 4.3.2).",

        // Written by iTunes and other programs, not defined by any ID3v2 version.
        ["TCMP"] = "Part of a compilation, 1 when it is. iTunes writes it; no ID3v2 version defines it.",
        ["TSO2"] = "Album artist sort order. iTunes writes it; no ID3v2 version defines it.",
        ["TSOC"] = "Composer sort order. iTunes writes it; no ID3v2 version defines it.",
        ["GRP1"] = "Grouping. iTunes writes it; no ID3v2 version defines it.",
        ["MVNM"] = "Movement name. iTunes writes it; no ID3v2 version defines it.",
        ["MVIN"] = "Movement number. iTunes writes it; no ID3v2 version defines it.",
    };

    // ID3v2.2 section 4 names the same frames with three letters.
    private static readonly Dictionary<string, string> V22Ids = new(StringComparer.Ordinal)
    {
        ["BUF"] = "RBUF",
        ["CNT"] = "PCNT",
        ["COM"] = "COMM",
        ["CRA"] = "AENC",
        ["ETC"] = "ETCO",
        ["EQU"] = "EQUA",
        ["GEO"] = "GEOB",
        ["IPL"] = "IPLS",
        ["LNK"] = "LINK",
        ["MCI"] = "MCDI",
        ["MLL"] = "MLLT",
        ["PIC"] = "APIC",
        ["POP"] = "POPM",
        ["REV"] = "RVRB",
        ["RVA"] = "RVAD",
        ["SLT"] = "SYLT",
        ["STC"] = "SYTC",
        ["TAL"] = "TALB",
        ["TBP"] = "TBPM",
        ["TCM"] = "TCOM",
        ["TCO"] = "TCON",
        ["TCR"] = "TCOP",
        ["TDA"] = "TDAT",
        ["TDY"] = "TDLY",
        ["TEN"] = "TENC",
        ["TFT"] = "TFLT",
        ["TIM"] = "TIME",
        ["TKE"] = "TKEY",
        ["TLA"] = "TLAN",
        ["TLE"] = "TLEN",
        ["TMT"] = "TMED",
        ["TOA"] = "TOPE",
        ["TOF"] = "TOFN",
        ["TOL"] = "TOLY",
        ["TOR"] = "TORY",
        ["TOT"] = "TOAL",
        ["TP1"] = "TPE1",
        ["TP2"] = "TPE2",
        ["TP3"] = "TPE3",
        ["TP4"] = "TPE4",
        ["TPA"] = "TPOS",
        ["TPB"] = "TPUB",
        ["TRC"] = "TSRC",
        ["TRD"] = "TRDA",
        ["TRK"] = "TRCK",
        ["TSI"] = "TSIZ",
        ["TSS"] = "TSSE",
        ["TT1"] = "TIT1",
        ["TT2"] = "TIT2",
        ["TT3"] = "TIT3",
        ["TXT"] = "TEXT",
        ["TXX"] = "TXXX",
        ["TYE"] = "TYER",
        ["UFI"] = "UFID",
        ["ULT"] = "USLT",
        ["WAF"] = "WOAF",
        ["WAR"] = "WOAR",
        ["WAS"] = "WOAS",
        ["WCM"] = "WCOM",
        ["WCP"] = "WCOP",
        ["WPB"] = "WPUB",
        ["WXX"] = "WXXX",
    };

    private static readonly Dictionary<string, string> V22Frames = V22Ids.ToDictionary(
        p => p.Key,
        p => $"The ID3v2.2 form of {p.Value}. {Frames[p.Value]}",
        StringComparer.Ordinal);

    private const string EncryptedMeta = "Encrypted meta frame: other frames, encrypted together (ID3v2.2).";

    public static string For(int major, string id)
    {
        if (major == 2)
        {
            return id == "CRM" ? EncryptedMeta : V22Frames.GetValueOrDefault(id) ?? (id[0] is 'X' or 'Y' or 'Z' ? Experimental : Unknown);
        }

        return Frames.GetValueOrDefault(id) ?? (id[0] is 'X' or 'Y' or 'Z' ? Experimental : Unknown);
    }

    // The topic of a frame whose content has one beyond the tag's.
    public static string? TopicFor(int major, string id) => (major == 2 && V22Ids.TryGetValue(id, out string? v23) ? v23 : id) switch
    {
        "TSSE" => Topics.EncoderDetection,
        _ => null,
    };
}

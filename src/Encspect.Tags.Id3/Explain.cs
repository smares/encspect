namespace Encspect.Tags.Id3;

// The background articles the ID3 nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Id3v1 = "id3v1";
    public const string Id3v2 = "id3v2";
    public const string Unsynchronization = "id3v2-unsynchronization";
    public const string Padding = "id3v2-padding";
    public const string TextEncodings = "id3v2-text-encodings";
    public const string EncoderDetection = "encoder-detection";
}

// What each ID3 node is. Constants only, shared by every node of their kind.
internal static class Explain
{
    public const string Id3v1Tag = "An ID3v1 tag: the last 128 bytes of the file, with fixed fields for title, artist, album, year, comment and genre; ID3v1.1 takes a track number from the end of the comment.";
    public const string Id3v1Signature = "The signature \"TAG\" that starts an ID3v1 tag.";
    public const string Id3v1Title = "The title, up to 30 characters in ISO-8859-1, padded with zero bytes or spaces.";
    public const string Id3v1Artist = "The artist, up to 30 characters in ISO-8859-1.";
    public const string Id3v1Album = "The album, up to 30 characters in ISO-8859-1.";
    public const string Id3v1Year = "The year, four characters.";
    public const string Id3v1Comment = "A comment of up to 30 characters, or 28 in ID3v1.1, which keeps the last two bytes for a zero byte and the track number.";
    public const string Id3v1Track = "The track number, which ID3v1.1 stores in the last byte of the comment, after a zero byte.";
    public const string Id3v1Genre = "A genre by its number in the list ID3v1 defines, which Winamp extended; 255 means none.";

    public const string EnhancedTag = "The Enhanced TAG, 227 bytes directly before an ID3v1 tag, with longer title, artist and album fields, a speed, a free-text genre and start and end times. No authoritative specification exists.";
    public const string EnhancedSignature = "The signature \"TAG+\" that starts an Enhanced TAG.";
    public const string EnhancedTitle = "60 more characters of the title, after the 30 in the ID3v1 tag.";
    public const string EnhancedArtist = "60 more characters of the artist, after the 30 in the ID3v1 tag.";
    public const string EnhancedAlbum = "60 more characters of the album, after the 30 in the ID3v1 tag.";
    public const string EnhancedSpeed = "A speed for players to show: 0 unset, 1 slow, 2 medium, 3 fast, 4 hardcore.";
    public const string EnhancedGenre = "A genre as free text, up to 30 characters.";
    public const string EnhancedStart = "Where the music starts in the file, as mmm:ss.";
    public const string EnhancedEnd = "Where the music ends in the file, as mmm:ss.";

    public const string Id3v2Tag = "An ID3v2 tag: a header, an optional extended header, frames that each hold one piece of metadata, then padding or a footer (id3.org, ID3v2.2 to 2.4).";
    public const string Header = "The 10-byte tag header: \"ID3\", the version, flags, and the size of the tag after it (ID3v2.4.0 section 3.1).";
    public const string Footer = "The 10-byte footer, which repeats the header after \"3DI\" so the tag can be found from its end (ID3v2.4.0 section 3.4).";
    public const string HeaderIdentifier = "The signature: \"ID3\" in the header, \"3DI\" in a footer.";
    public const string HeaderVersion = "The major version and the revision. The major version decides how frames are laid out; a reader that does not know it skips the tag.";
    public const string HeaderFlags = "The tag's flags, one bit each.";
    public const string IsUnsynchronized = "Whether the tag is unsynchronized: a zero byte follows every 0xFF that could look like an MPEG sync, and readers remove it.";
    public const string IsCompressedTag = "Whether the tag is compressed. ID3v2.2 never defined a compression scheme, so no reader can decode such a tag.";
    public const string HasExtendedHeader = "Whether an extended header follows the header.";
    public const string IsExperimental = "Whether the tag is marked as experimental.";
    public const string HasFooter = "Whether a footer ends the tag (ID3v2.4), so it can be found from the end of a file.";
    public const string Size = "The size of the tag after the header, without a footer, as a syncsafe integer: 28 bits in four bytes whose top bits are clear, so it never looks like an MPEG sync.";
    public const string CompressedTag = "The tag's body, which the header marks as compressed and which is not read.";

    public const string ExtendedHeader = "Optional data about the tag itself: a CRC, the padding size (ID3v2.3), or the update and restriction flags (ID3v2.4).";
    public const string ExtendedSize = "The extended header's size: without these four bytes in ID3v2.3, with them and syncsafe in ID3v2.4.";
    public const string ExtendedFlags23 = "The extended header's flags; ID3v2.3 defines only the CRC flag.";
    public const string ExtendedFlags24 = "The extended header's flags; each set flag adds its data after them.";
    public const string ExtendedHasCrc = "Whether a CRC-32 of the tag's data follows.";
    public const string ExtendedPadding = "How many bytes of padding the tag holds, as the extended header declares (ID3v2.3).";
    public const string ExtendedData = "Bytes at the end of the extended header that its flags do not account for.";
    public const string ExtendedCrc = "A CRC-32 of the frames (ID3v2.3) or of everything after the extended header (ID3v2.4), which shows whether the tag changed after it was written.";
    public const string ComputedCrc = "The CRC-32 the analyzer computed over the same data.";
    public const string FlagBytes = "How many bytes of flags follow; ID3v2.4 defines one.";
    public const string IsUpdate = "Whether this tag updates an earlier tag in the file instead of replacing it.";
    public const string HasRestrictions = "Whether the tag declares limits on its size and content.";
    public const string UpdateData = "The data of the update flag, which is empty.";
    public const string Restrictions = "Limits the tagger promises to keep: the tag's size, the text encoding and length, and the image format and size (ID3v2.4.0 section 3.2).";
    public const string RestrictTagSize = "The most frames and bytes the tag may hold.";
    public const string RestrictTextEncoding = "Whether text is limited to ISO-8859-1 and UTF-8.";
    public const string RestrictTextSize = "The most characters a string may hold.";
    public const string RestrictImageEncoding = "Whether pictures are limited to PNG and JPEG.";
    public const string RestrictImageSize = "The largest size a picture may have.";

    public const string FrameHeader = "The frame header: the frame ID, the size and, in ID3v2.3 and 2.4, two bytes of flags (ID3v2.4.0 section 4.1).";
    public const string FrameId = "The frame ID, four characters (three in ID3v2.2), which says what the frame holds.";
    public const string FrameSize = "The size of the frame after its header: a syncsafe integer in ID3v2.4, a plain one in ID3v2.2 and 2.3.";
    public const string FrameFlags = "The frame's status and format flags.";
    public const string IsDiscardedOnTagAlter = "Whether software that changes the tag and does not know this frame should drop it.";
    public const string IsDiscardedOnFileAlter = "Whether software that changes the audio and does not know this frame should drop it, as for a frame that describes the audio.";
    public const string IsReadOnly = "Whether the frame is meant not to be changed.";
    public const string IsCompressed = "Whether the frame's data is zlib-compressed.";
    public const string IsEncrypted = "Whether the frame's data is encrypted with a method an ENCR frame registers.";
    public const string HasGroupId = "Whether a group ID ties this frame to others, as a GRID frame registers.";
    public const string IsFrameUnsynchronized = "Whether this frame is unsynchronized; ID3v2.4 applies unsynchronization frame by frame.";
    public const string HasDataLengthIndicator = "Whether the frame's size with compression, encryption and unsynchronization undone follows the header.";
    public const string DecompressedSize = "The frame's size after decompression.";
    public const string EncryptionMethod = "The encryption method, as an ENCR frame registers it.";
    public const string GroupId = "The group this frame belongs to, as a GRID frame registers it.";
    public const string DataLength = "The frame's size with compression, encryption and unsynchronization undone.";
    public const string EncryptedData = "The frame's encrypted content, which is not decoded.";
    public const string CompressedData = "The frame's zlib-compressed content; the fields after it are decompressed from it.";
    public const string SeekData = "The content of a SEEK frame that does not hold the four bytes it should.";
    public const string MinimumOffset = "Bytes from the end of this tag at least to the next tag (ID3v2.4.0 frames section 4.29).";
    public const string Padding = "Zero bytes after the frames, which leave room to change the tag without rewriting the whole file.";
    public const string Unparsed = "Bytes of the tag the analyzer could not read as frames.";

    public const string Encoding = "The text encoding of the frame's strings: ISO-8859-1, UTF-16 with a byte order mark, or, in ID3v2.4 only, UTF-16BE without one or UTF-8.";
    public const string Text = "The frame's text. ID3v2.4 allows several values, each ended by a terminator.";
    public const string Description = "A short description that tells this frame from others of the same kind.";
    public const string Url = "The address the frame links to, always in ISO-8859-1.";
    public const string Language = "The language of the text, as a three-letter ISO 639-2 code.";
    public const string ImageFormat = "The picture's format in three letters, such as PNG or JPG (ID3v2.2).";
    public const string MimeType = "The media type of the data, such as image/jpeg; \"-->\" in a picture frame means the data is a link to the picture.";
    public const string PictureType = "What the picture shows, from the specification's list of 21 types, such as the front cover.";
    public const string PictureData = "The picture's bytes.";
    public const string Owner = "Who defined the data: an email address or a URL that identifies the organization or program.";
    public const string Identifier = "The identifier the owner gave the file, up to 64 bytes.";
    public const string PrivateData = "Data in a format only its owner defines.";
    public const string FrameData = "The frame's content, which the analyzer does not decode.";
    public const string Email = "The email address of the user the rating belongs to.";
    public const string Rating = "The rating, from 1 to 255, higher for tracks the user likes more; 0 means unknown.";
    public const string Counter = "How many times the file was played.";
    public const string Filename = "The file name of the embedded object.";
    public const string ObjectData = "The bytes of the embedded object.";
}

namespace Encspect.Formats.Mp4;

// The background articles the MP4 nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string IsoBaseMediaFormat = "iso-base-media-format";
    public const string SampleTables = "mp4-sample-tables";
    public const string EditListsAndPriming = "edit-lists-and-priming";
    public const string ItunesMetadata = "itunes-metadata";
    public const string FragmentedMp4 = "fragmented-mp4";
    public const string AudioSpecificConfig = "audio-specific-config";
    public const string GaplessPlayback = "gapless-playback";
    public const string BitrateModes = "bitrate-modes";
    public const string Id3v2 = "id3v2";
    public const string ChannelLayouts = "channel-layouts";
    public const string CommonEncryption = "common-encryption";
    public const string FairPlayAndAudible = "fairplay-and-audible";
}

// What each MP4 node is, in the words the report shows next to it. Constants only, picked per box
// type, item type or value; nothing is built per box.
internal static class Explain
{
    public const string File = "An ISO base media file: a run of boxes, each a size, a four-character type and its contents. MP4, M4A, M4B and QuickTime .mov files share this structure (ISO/IEC 14496-12 4.2).";
    public const string TrailingData = "Bytes after the last box that do not start another box, such as a tag appended to the file or the rest of a damaged box.";

    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies inside MP4 data without a declared end, which the tag breaks; the analyzer does not read them as boxes.";
    public const string UnusedBytes = "Bytes inside a box that are too few for a box header or that a damaged size left over; they are not read as boxes.";
    public const string Payload = "The box's contents, not read because the box nests deeper than the analyzer follows.";
    public const string UnreadData = "The rest of the box's contents, whose fields the analyzer does not read.";
    public const string MediaData = "The coded media itself. The sample tables say where each sample lies in it; the bytes have no structure of their own (ISO/IEC 14496-12 8.1.1).";
    public const string FreeData = "Unused space that a writer reserved, for example so the moov box can grow without moving the media data. Readers skip it (ISO/IEC 14496-12 8.1.2).";
    public const string MoreEntries = "The table's further entries, not listed one by one. They are still read and used.";
    public const string Reserved = "Reserved bytes, which the specification sets to zero.";

    public const string Size = "The box's size in bytes, header included. 1 means a 64-bit size follows the type; 0 means the box runs to the end of the file (ISO/IEC 14496-12 4.2).";
    public const string Type = "The box's four-character type, which says what the box holds and how to read it (ISO/IEC 14496-12 4.2).";
    public const string LargeSize = "The box's size as a 64-bit number, for boxes of 4 GiB or more (ISO/IEC 14496-12 4.2).";
    public const string UserType = "The 16-byte UUID that names an extension box of type uuid, which anyone can define without registering a type (ISO/IEC 14496-12 4.2).";
    public const string Version = "The version of the box's layout. Version 1 usually widens times and durations to 64 bits (ISO/IEC 14496-12 4.2).";
    public const string Flags = "24 bits of flags whose meaning depends on the box type (ISO/IEC 14496-12 4.2).";

    public const string MajorBrand = "The brand, a four-character code, of the specification the file best follows, such as M4A for Apple audio or isom for the ISO base media format (ISO/IEC 14496-12 4.3).";
    public const string MinorVersion = "The version of the major brand; informative only (ISO/IEC 14496-12 4.3).";
    public const string CompatibleBrand = "A brand whose specification the file also follows; a reader that knows one of the brands can read the file (ISO/IEC 14496-12 4.3).";

    public const string CreationTime = "When the media was created, in seconds since midnight, January 1, 1904, UTC. Many writers leave it 0 (ISO/IEC 14496-12 8.2.2.3).";
    public const string ModificationTime = "When the media was last changed, in seconds since midnight, January 1, 1904, UTC (ISO/IEC 14496-12 8.2.2.3).";
    public const string MovieTimescale = "The number of time units per second for the movie's durations, such as the edit lists' and the track headers' (ISO/IEC 14496-12 8.2.2.3).";
    public const string MovieDuration = "The length of the presentation, that of its longest track, in the movie's timescale (ISO/IEC 14496-12 8.2.2.3).";
    public const string Rate = "The preferred playback rate, 1.0 for normal speed (ISO/IEC 14496-12 8.2.2.3).";
    public const string MovieVolume = "The preferred playback volume, 1.0 for full volume (ISO/IEC 14496-12 8.2.2.3).";
    public const string Matrix = "A transformation matrix for video: how to scale, rotate and place the picture. Audio-only files carry the identity matrix (ISO/IEC 14496-12 8.2.2.3).";
    public const string NextTrackId = "The ID a new track added to the file would take: one more than the largest in use (ISO/IEC 14496-12 8.2.2.3).";

    public const string IsEnabled = "Whether the track is enabled, flag 1 of the track header. A disabled track is not played (ISO/IEC 14496-12 8.3.2.3).";
    public const string TrackId = "The number that identifies the track in this file, unique and never 0 (ISO/IEC 14496-12 8.3.2.3).";
    public const string TrackDuration = "The track's presented length in the movie's timescale: the sum of its edits, or the media's length where it has none (ISO/IEC 14496-12 8.3.2.3).";
    public const string Layer = "The front-to-back order of video tracks; lower numbers are closer to the viewer (ISO/IEC 14496-12 8.3.2.3).";
    public const string AlternateGroup = "Tracks with the same nonzero group are alternatives of which a player plays one, such as audio in different languages; 0 means the track is in no group (ISO/IEC 14496-12 8.3.2.3).";
    public const string TrackVolume = "The track's relative audio volume, 1.0 for full volume and 0 for a track that is not audio (ISO/IEC 14496-12 8.3.2.3).";
    public const string Width = "The visual width of the track, 0 for audio (ISO/IEC 14496-12 8.3.2.3).";
    public const string Height = "The visual height of the track, 0 for audio (ISO/IEC 14496-12 8.3.2.3).";

    public const string MediaTimescale = "The number of time units per second for the media's durations and the sample tables' times. Audio tracks usually take their sample rate (ISO/IEC 14496-12 8.4.2.3).";
    public const string MediaDuration = "The length of the media in its own timescale: the sum of its samples' durations, before any edit list (ISO/IEC 14496-12 8.4.2.3).";
    public const string Language = "The language of the media, three letters of ISO 639-2/T packed in 5 bits each, or below 0x400 a Macintosh language code, which QuickTime uses (ISO/IEC 14496-12 8.4.2.3; QuickTime File Format, Language code values).";
    public const string Quality = "QuickTime's playback quality for the media; ISO/IEC 14496-12 makes these 16 bits pre_defined and 0.";

    public const string ComponentType = "QuickTime's component type, mhlr for a media handler and dhlr for a data handler; ISO/IEC 14496-12 makes this field pre_defined and 0.";
    public const string HandlerType = "What the track holds, such as soun for sound and vide for video, or in a meta box the format of the metadata, such as mdir for iTunes metadata (ISO/IEC 14496-12 8.4.3.3).";
    public const string HandlerName = "A name for the handler, for people to read. Writers often put their own name here.";

    public const string Balance = "The audio's balance between the speakers, from -1.0 for fully left to 1.0 for fully right; 0 is centered (ISO/IEC 14496-12 12.2.2).";
    public const string DataEntries = "The number of data references that follow, of which a sample description picks one by its index (ISO/IEC 14496-12 8.7.2).";
    public const string DataLocation = "The URL of the file that holds the media data, where it is not in this file (ISO/IEC 14496-12 8.7.2).";
    public const string DataName = "The URN that names the media data's location (ISO/IEC 14496-12 8.7.2).";

    public const string EditEntries = "The number of edits in the list (ISO/IEC 14496-12 8.6.6).";
    public const string Edit = "One edit: a stretch of the presentation, and where in the media it starts. Edits play one after another (ISO/IEC 14496-12 8.6.6).";
    public const string SegmentDuration = "How long this edit plays, in the movie's timescale (ISO/IEC 14496-12 8.6.6.3).";
    public const string MediaTime = "Where in the media this edit starts, in the media's timescale; -1 marks an empty edit, a pause. For AAC it is usually the encoder delay, the priming samples a player skips (ISO/IEC 14496-12 8.6.6.3).";
    public const string MediaRate = "The rate at which the edit plays its media, 1.0 for normal speed; 0 holds one moment, a dwell (ISO/IEC 14496-12 8.6.6.3).";

    public const string SampleEntryCount = "The number of sample descriptions that follow; each sample points to one through the sample-to-chunk table (ISO/IEC 14496-12 8.5.2).";
    public const string SampleEntryReserved = "Six reserved bytes that open every sample entry, set to zero (ISO/IEC 14496-12 8.5.2.2).";
    public const string DataReferenceIndex = "Which data reference holds the samples that use this description, counted from 1 (ISO/IEC 14496-12 8.5.2.2).";
    public const string SampleEntryData = "The rest of the sample description, which the analyzer does not read for this kind of track.";
    public const string SoundVersion = "The version of QuickTime's sound sample description: 0, or 1 and 2, which add fields. ISO/IEC 14496-12 keeps these bytes reserved for MP4 (QuickTime File Format, Sound sample descriptions).";
    public const string RevisionLevel = "QuickTime's revision level of the sound description, 0 (QuickTime File Format, Sound sample descriptions).";
    public const string Vendor = "A code for the developer of the compressor that wrote the samples, 0 in most files (QuickTime File Format, Sound sample descriptions).";
    public const string Channels = "The number of audio channels the sample entry declares. For AAC the decoder takes the channels from the codec's own configuration (ISO/IEC 14496-12 12.2.3).";
    public const string SampleSize = "The bits per uncompressed sample, 16 by default. It does not describe compressed audio such as AAC (ISO/IEC 14496-12 12.2.3).";
    public const string CompressionId = "QuickTime's compression ID: 0 for audio the sample tables count in uncompressed samples, -2 where they count compressed packets, as QuickTime requires for MPEG-4 audio (QuickTime File Format, MPEG-4 audio).";
    public const string PacketSize = "QuickTime's packet size, 0 (QuickTime File Format, Sound sample descriptions).";
    public const string EntrySampleRate = "The sample rate the sample entry declares, a 16.16 fixed-point number. For AAC with SBR it may be the core's half rate or the output rate, depending on the writer (ISO/IEC 14496-12 12.2.3).";
    public const string SamplesPerPacket = "The number of uncompressed frames, one sample per channel, that one compressed packet decodes to (QuickTime File Format, Sound sample description version 1).";
    public const string BytesPerPacket = "The bytes of one channel's uncompressed sample, as QuickTime's version 1 describes the data (QuickTime File Format, Sound sample description version 1).";
    public const string BytesPerFrame = "The bytes of one uncompressed frame, all channels together (QuickTime File Format, Sound sample description version 1).";
    public const string BytesPerSample = "The bytes of one uncompressed sample (QuickTime File Format, Sound sample description version 1).";
    public const string StructureSize = "The size of the version 2 sound description before its extensions, where the boxes that extend it start (QuickTime File Format, Sound sample description version 2).";
    public const string AudioSampleRate = "The sample rate as a 64-bit floating-point number, which version 2 uses for rates the 16.16 field cannot hold (QuickTime File Format, Sound sample description version 2).";
    public const string AudioChannels = "The number of channels, which version 2 gives in 32 bits (QuickTime File Format, Sound sample description version 2).";
    public const string AlwaysValue = "A constant that version 2 places here, 0x7F000000 (QuickTime File Format, Sound sample description version 2).";
    public const string BitsPerChannel = "The bits per uncompressed channel sample, 0 for compressed audio (QuickTime File Format, Sound sample description version 2).";
    public const string FormatFlags = "Core Audio's format flags for the audio, such as float or big-endian for PCM; 0 for compressed audio (QuickTime File Format, Sound sample description version 2).";
    public const string BytesPerAudioPacket = "The bytes of one compressed packet, 0 where packets vary in size (QuickTime File Format, Sound sample description version 2).";
    public const string FramesPerAudioPacket = "The uncompressed frames one packet decodes to, such as 1024 for AAC LC (QuickTime File Format, Sound sample description version 2).";

    public const string DescriptorTag = "The descriptor's tag, which says what kind of descriptor it is: 3 an ES_Descriptor, 4 a DecoderConfigDescriptor, 5 a DecoderSpecificInfo, 6 an SLConfigDescriptor (ISO/IEC 14496-1).";
    public const string DescriptorLength = "The descriptor's length in bytes, in one to four bytes of seven bits each, the top bit set on all but the last (ISO/IEC 14496-1).";
    public const string EsDescriptor = "The elementary stream descriptor: the stream's ID and the decoder's configuration (ISO/IEC 14496-1 7.2.6.5).";
    public const string DecoderConfigDescriptor = "What decoder the stream needs: its object type, the stream type, the decoder's buffer size and the bitrates (ISO/IEC 14496-1 7.2.6.6).";
    public const string DecoderSpecificInfo = "The configuration only the decoder reads. For MPEG-4 audio it holds the AudioSpecificConfig (ISO/IEC 14496-1 7.2.6.7; ISO/IEC 14496-3 1.6.2.1).";
    public const string SlConfigDescriptor = "The sync layer configuration, which MP4 files fix to the predefined value 2 (ISO/IEC 14496-1; ISO/IEC 14496-14).";
    public const string OtherDescriptor = "A descriptor the analyzer does not read (ISO/IEC 14496-1 7.2).";
    public const string DescriptorData = "The descriptor's contents, which the analyzer does not read further.";
    public const string EsId = "The elementary stream's ID (ISO/IEC 14496-1 7.2.6.5).";
    public const string EsFlags = "Flags of the elementary stream descriptor: whether a dependency, a URL or an OCR stream follows, and the stream's priority (ISO/IEC 14496-1 7.2.6.5).";
    public const string DependsOnEsId = "The ID of the stream this stream depends on (ISO/IEC 14496-1 7.2.6.5).";
    public const string UrlLength = "The length of the URL that follows (ISO/IEC 14496-1 7.2.6.5).";
    public const string Url = "A URL where the stream's data is (ISO/IEC 14496-1 7.2.6.5).";
    public const string OcrEsId = "The ID of the stream whose clock this stream follows (ISO/IEC 14496-1 7.2.6.5).";
    public const string ObjectTypeIndication = "The coding of the stream: 0x40 for MPEG-4 audio, with the object type in its AudioSpecificConfig, 0x66 to 0x68 for MPEG-2 AAC, 0x69 and 0x6B for MPEG-2 and MPEG-1 audio (ISO/IEC 14496-1 7.2.6.6; MP4 registration authority).";
    public const string SchemeType = "The protection scheme, a four-character code such as cenc for Common Encryption's AES-CTR (ISO/IEC 14496-12 8.12.5; the MP4 registration authority lists the codes).";
    public const string SchemeVersion = "The version of the scheme; Common Encryption files write 0x00010000 (ISO/IEC 14496-12 8.12.5).";
    public const string SchemeUri = "A URI where more about the scheme can be found, present where flag 1 is set (ISO/IEC 14496-12 8.12.5).";
    public const string CryptByteBlock = "In a pattern scheme (cens, cbcs), how many 16-byte blocks of each pattern are encrypted; 0 with a skip of 0 means every block (ISO/IEC 23001-7).";
    public const string SkipByteBlock = "In a pattern scheme (cens, cbcs), how many 16-byte blocks of each pattern are left in the clear after the encrypted ones (ISO/IEC 23001-7).";
    public const string DefaultIsProtected = "Whether the track's samples are encrypted unless a sample group says otherwise; false is how a clear lead-in or a track with no encryption is marked (ISO/IEC 23001-7).";
    public const string DefaultIvSize = "The size in bytes of the initialization vector each sample carries: 0 where one constant vector serves every sample, else 8 or 16 (ISO/IEC 23001-7).";
    public const string DefaultKid = "The ID of the key that encrypts the track's samples by default, a 16-byte identifier shown as a UUID; the key itself is not in the file (ISO/IEC 23001-7).";
    public const string ConstantIvSize = "The size in bytes of the constant initialization vector, 8 or 16 (ISO/IEC 23001-7).";
    public const string ConstantIv = "The initialization vector every sample uses where samples carry none of their own, as hexadecimal (ISO/IEC 23001-7).";
    public const string SystemId = "The ID of the DRM system the box is for, a UUID; DASH-IF's registry names the systems that submitted one (ISO/IEC 23001-7).";
    public const string PsshKidCount = "How many key IDs the box lists, in a version 1 box (ISO/IEC 23001-7).";
    public const string PsshKid = "The ID of a key the system's data covers (ISO/IEC 23001-7).";
    public const string PsshDataSize = "The size in bytes of the system's own data (ISO/IEC 23001-7).";
    public const string PsshData = "Data only the named DRM system reads, such as what a license request needs; not read here (ISO/IEC 23001-7).";
    public const string UsesSubsampleEncryption = "Whether each sample lists clear and encrypted ranges (subsamples), so that only part of it is encrypted (ISO/IEC 23001-7).";
    public const string SencSampleCount = "The number of samples whose initialization vectors the box lists (ISO/IEC 23001-7).";
    public const string SencEntries = "The samples' initialization vectors and subsample ranges, whose layout depends on the track's initialization vector size; not read here (ISO/IEC 23001-7).";
    public const string AuxInfoType = "The kind of auxiliary information the box describes, such as cenc for Common Encryption's initialization vectors (ISO/IEC 14496-12 8.7.8).";
    public const string AuxInfoTypeParameter = "A parameter that tells apart auxiliary information of the same type (ISO/IEC 14496-12 8.7.8).";
    public const string DefaultSampleInfoSize = "The size in bytes of every sample's auxiliary information, or 0 where the sizes are listed one by one (ISO/IEC 14496-12 8.7.8).";
    public const string SaizSampleCount = "The number of samples the box gives auxiliary information sizes for (ISO/IEC 14496-12 8.7.8).";
    public const string SaizSizes = "The size in bytes of each sample's auxiliary information, one byte per sample (ISO/IEC 14496-12 8.7.8).";
    public const string SaioEntries = "The number of offsets that follow, one for each chunk or one for the whole run (ISO/IEC 14496-12 8.7.9).";
    public const string SaioOffset = "Where a chunk's or run's auxiliary information starts, relative to the file or fragment as the box's context says (ISO/IEC 14496-12 8.7.9).";
    public const string SampleEncryptionGroupEntry = "A group of samples with their own encryption parameters, which override the track's defaults (ISO/IEC 23001-7).";
    public const string GroupIsProtected = "Whether the samples of the group are encrypted (ISO/IEC 23001-7).";
    public const string GroupKid = "The ID of the key that encrypts the group's samples (ISO/IEC 23001-7).";
    public const string FairPlayData = "Data of Apple's iTunes Store protection (FairPlay), whose layout no source the analyzer follows describes; it is not read.";
    public const string AdrmLeading = "The first eight bytes of Audible's DRM box, which FFmpeg's reader skips (libavformat/mov.c, mov_read_adrm).";
    public const string AdrmBlob = "A 56-byte blob that FFmpeg decrypts with a key it derives from the account's activation bytes; not read here (libavformat/mov.c, mov_read_adrm).";
    public const string AdrmSkipped = "Four bytes between the blob and the checksum, which FFmpeg's reader skips (libavformat/mov.c, mov_read_adrm).";
    public const string AdrmChecksum = "20 bytes that FFmpeg compares with a SHA-1 it derives from the key and the activation bytes (libavformat/mov.c, mov_read_adrm).";
    public const string EncryptedStream = "The stream of a track whose samples are encrypted, described by its sample entry and sample tables; its frames are not read.";
    public const string SourceBitDepth = "The bit depth of the source audio as afconvert's files spell it: I for integer samples and the number of bits (I16 in the files seen).";

    public const string StreamType = "The kind of stream, 5 for audio, whether it flows upstream, and a reserved bit that ISO/IEC 14496-1 7.2.6.6 sets to 1 (afconvert's files have 0).";
    public const string DecoderBufferSize = "The size of the decoder's input buffer the stream needs, in bytes (ISO/IEC 14496-1 7.2.6.6).";
    public const string DecoderMaxBitrate = "The highest bitrate of the stream over any one-second window, as the writer states it (ISO/IEC 14496-1 7.2.6.6).";
    public const string DecoderAverageBitrate = "The stream's average bitrate as the writer states it; 0 marks a variable bitrate (ISO/IEC 14496-1 7.2.6.6).";
    public const string SlPredefined = "Which predefined sync layer configuration applies: 2 is the one reserved for MP4 files (ISO/IEC 14496-1; ISO/IEC 14496-14).";

    public const string ChanLayoutTag = "mChannelLayoutTag: a layout Core Audio defines, named by Apple's constant with its channels in order (CoreAudioBaseTypes.h, AudioChannelLayoutTag); UseChannelBitmap and UseChannelDescriptions defer to the fields after it. The lower 16 bits give the channel count.";
    public const string ChanBitmap = "mChannelBitmap: one bit per speaker, in the order of the WAVE channel mask, which gives the channels where the layout tag is UseChannelBitmap, lowest bit first (CoreAudioBaseTypes.h, AudioChannelBitmap).";
    public const string ChanDescriptionCount = "mNumberChannelDescriptions: how many channel descriptions follow, one per channel where the layout tag is UseChannelDescriptions (CoreAudioBaseTypes.h, AudioChannelLayout).";
    public const string ChanDescription = "AudioChannelDescription: one channel's label, flags and coordinates, in the stream's channel order (CoreAudioBaseTypes.h).";
    public const string ChanLabel = "mChannelLabel: what the channel feeds, such as Left or LeftSurround, named by Apple's constant (CoreAudioBaseTypes.h, AudioChannelLabel).";
    public const string ChanFlags = "mChannelFlags: whether the coordinates are rectangular (1) or spherical (2), and in meters (4) (CoreAudioBaseTypes.h, AudioChannelFlags).";
    public const string ChanCoordinates = "mCoordinates: a speaker position as three 32-bit floats, left/right, back/front and down/up, or azimuth, elevation and distance, as the flags say; unused where neither flag is set (CoreAudioBaseTypes.h).";
    public const string ChnlStreamStructure = "stream_structure: 1 where the track carries channels, 2 where it carries audio objects, 3 for both (ISO/IEC 14496-12 12.2.4).";
    public const string ChnlDefinedLayout = "definedLayout: a ChannelConfiguration of ISO/IEC 23091-3, such as 6 for 5.1 with the center first, or 0 where a speaker position follows for each channel of the sample entry (ISO/IEC 14496-12 12.2.4).";
    public const string ChnlSpeakerPosition = "speaker_position: the speaker one channel feeds, an OutputChannelPosition of ISO/IEC 23091-3, in the stream's channel order; 126 means an explicit azimuth and elevation follow (ISO/IEC 14496-12 12.2.4).";
    public const string ChnlAzimuth = "azimuth: the explicit speaker's direction in degrees from the front (ISO/IEC 14496-12 12.2.4).";
    public const string ChnlElevation = "elevation: the explicit speaker's height in degrees above the listener's plane (ISO/IEC 14496-12 12.2.4).";
    public const string ChnlOmittedChannels = "omittedChannelsMap: the defined layout's channels the track leaves out, one bit each, the lowest bit for its first channel (ISO/IEC 14496-12 12.2.4).";
    public const string ChnlObjectCount = "object_count: how many audio objects the track carries (ISO/IEC 14496-12 12.2.4).";
    public const string BtrtBufferSize = "The size of the decoder's buffer the stream needs, in bytes (ISO/IEC 14496-12 8.5.2.2).";
    public const string BtrtMaxBitrate = "The highest bitrate over any one-second window, in bits per second (ISO/IEC 14496-12 8.5.2.2).";
    public const string BtrtAverageBitrate = "The average bitrate over the whole stream, in bits per second (ISO/IEC 14496-12 8.5.2.2).";
    public const string OriginalFormat = "The four-character code of the sample entry's format before it was wrapped, such as mp4a (ISO/IEC 14496-12 8.12.2).";

    public const string TimeToSampleEntries = "The number of runs in the table, each a count of samples that share one duration (ISO/IEC 14496-12 8.6.1.2).";
    public const string TimeToSampleEntry = "A run of consecutive samples that last the same time (ISO/IEC 14496-12 8.6.1.2).";
    public const string RunSampleCount = "The number of consecutive samples in this run (ISO/IEC 14496-12 8.6.1.2).";
    public const string SampleDelta = "How long each sample of the run lasts, in the media's timescale. For AAC that is the samples per channel each access unit decodes to, such as 1024 (ISO/IEC 14496-12 8.6.1.2).";
    public const string CompositionEntries = "The number of runs of samples that share one composition offset (ISO/IEC 14496-12 8.6.1.3).";
    public const string CompositionEntry = "A run of consecutive samples whose composition time is offset from their decoding time by the same amount (ISO/IEC 14496-12 8.6.1.3).";
    public const string CompositionRunCount = "The number of consecutive samples in this run (ISO/IEC 14496-12 8.6.1.3).";
    public const string CompositionOffset = "How much later than its decoding time a sample is presented, in the media's timescale; used by video whose frames are coded out of order (ISO/IEC 14496-12 8.6.1.3).";
    public const string ChunkEntries = "The number of entries in the table, each the first of a run of chunks that hold the same number of samples (ISO/IEC 14496-12 8.7.4).";
    public const string ChunkEntry = "From this chunk on, until the next entry's first chunk, every chunk holds this many samples with this description (ISO/IEC 14496-12 8.7.4).";
    public const string FirstChunk = "The first chunk this entry applies to, counted from 1 (ISO/IEC 14496-12 8.7.4).";
    public const string SamplesPerChunk = "How many samples each of these chunks holds (ISO/IEC 14496-12 8.7.4).";
    public const string SampleDescriptionIndex = "Which sample description in the stsd box these samples use, counted from 1 (ISO/IEC 14496-12 8.7.4).";
    public const string ConstantSampleSize = "The size of every sample in bytes where all are equal; 0 means each sample's size is listed (ISO/IEC 14496-12 8.7.3.2).";
    public const string SampleCount = "The number of samples in the track, which for audio are access units such as AAC frames (ISO/IEC 14496-12 8.7.3.2).";
    public const string EntrySize = "The size of one sample in bytes (ISO/IEC 14496-12 8.7.3.2).";
    public const string FieldSize = "The bits each sample size takes in this compact table: 4, 8 or 16 (ISO/IEC 14496-12 8.7.3.3).";
    public const string CompactEntries = "The sizes of the samples, packed at the field size each (ISO/IEC 14496-12 8.7.3.3).";
    public const string ChunkOffsetEntries = "The number of chunks in the track (ISO/IEC 14496-12 8.7.5).";
    public const string ChunkOffset = "Where in the file this chunk, a run of samples stored back to back, starts (ISO/IEC 14496-12 8.7.5).";
    public const string SyncSampleEntries = "The number of sync samples, those a decoder can start at (ISO/IEC 14496-12 8.6.2).";
    public const string SyncSampleNumber = "A sample a decoder can start decoding at, counted from 1 (ISO/IEC 14496-12 8.6.2).";

    public const string GroupingType = "The kind of sample group, such as roll for AAC's dependency on the access unit before (ISO/IEC 14496-12 8.9.2, 8.9.3).";
    public const string GroupingTypeParameter = "A further subdivision of the grouping type (ISO/IEC 14496-12 8.9.2).";
    public const string DefaultLength = "The length of every group description in bytes, 0 where each states its own (ISO/IEC 14496-12 8.9.3).";
    public const string DefaultSampleDescriptionIndex = "The group description that samples outside every listed group belong to (ISO/IEC 14496-12 8.9.3).";
    public const string GroupDescriptionEntries = "The number of group descriptions that follow (ISO/IEC 14496-12 8.9.3).";
    public const string DescriptionLength = "The length of the group description that follows, in bytes (ISO/IEC 14496-12 8.9.3).";
    public const string GroupDescription = "One group description, whose contents depend on the grouping type (ISO/IEC 14496-12 8.9.3).";
    public const string RollEntry = "A roll recovery or pre-roll description: how many samples around a sample a decoder needs (ISO/IEC 14496-12 10.1).";
    public const string RollDistance = "The number of samples (access units) a decoder must decode to play a sample of this group correctly; -1 means it needs the one before, as with AAC (ISO/IEC 14496-12 10.1).";
    public const string SampleToGroupEntries = "The number of runs of samples in the table (ISO/IEC 14496-12 8.9.2).";
    public const string SampleToGroupEntry = "A run of consecutive samples that belong to the same group description (ISO/IEC 14496-12 8.9.2).";
    public const string GroupSampleCount = "The number of consecutive samples in this run (ISO/IEC 14496-12 8.9.2).";
    public const string GroupDescriptionIndex = "Which group description these samples belong to, counted from 1; 0 means none (ISO/IEC 14496-12 8.9.2).";

    public const string KeyEntries = "The number of keys, which the item list's items refer to by index from 1 (QuickTime File Format, Metadata item keys atom).";
    public const string Key = "A metadata key, the name of an item in reverse-DNS form such as com.apple.quicktime.title (QuickTime File Format, Metadata item keys atom).";
    public const string KeySize = "The size of this key entry in bytes, size and namespace included (QuickTime File Format, Metadata item keys atom).";
    public const string KeyNamespace = "The namespace of the key, mdta for reverse-DNS names (QuickTime File Format, Metadata item keys atom).";
    public const string KeyValue = "The key's name (QuickTime File Format, Metadata item keys atom).";
    public const string QuickTimeItem = "A QuickTime metadata item, named by the key its type points to; it holds one or more data atoms (QuickTime File Format, Metadata item atom).";
    public const string MeanValue = "The reverse-DNS domain of a freeform item, com.apple.iTunes for Apple's own (Apple iTunes metadata, freeform atom).";
    public const string NameValue = "The name of a freeform item within its domain, such as iTunSMPB (Apple iTunes metadata, freeform atom).";
    public const string DataType = "The value's type from the well-known types: 1 is UTF-8 text, 13 JPEG, 14 PNG, 21 a signed integer, and 0 data whose layout the item's type implies (QuickTime File Format, Well-known types).";
    public const string Locale = "The value's country and language, 0 for the default (QuickTime File Format, Locale indicator).";
    public const string DataValue = "The item's value, read as its data type says (QuickTime File Format, Data atom).";
    public const string SmpbDelay = "The encoder delay iTunSMPB states: the priming samples, at the start of the decoded audio, that are not part of the source (Apple iTunes metadata, iTunSMPB).";
    public const string SmpbPadding = "The padding iTunSMPB states: the samples after the source that fill the last access unit (Apple iTunes metadata, iTunSMPB).";
    public const string SmpbSamples = "The number of samples of the source between the delay and the padding, as iTunSMPB states it (Apple iTunes metadata, iTunSMPB).";
    public const string ParamsVersion = "The version of Apple's encoder settings (Encoding Params, vers).";
    public const string ParamsMode = "The bitrate control mode Apple's encoder used: 0 constant, 1 long-term average, 2 constrained variable, 3 variable bitrate, as Core Audio numbers them (Encoding Params, acbf).";
    public const string ParamsBitrate = "The bitrate Apple's encoder targeted, in bits per second (Encoding Params, brat).";
    public const string ParamsCodecVersion = "The version of Apple's encoder (Encoding Params, cdcv).";
    public const string ParamsOther = "A setting of Apple's encoder that the analyzer does not name.";
    public const string ParamsKey = "The setting's four-character key.";
    public const string ParamsValue = "The setting's 32-bit value.";

    public const string Id3Language = "The language of the ID3v2 tag that follows, three letters of ISO 639-2/T packed in 5 bits each (ID3v2 in ISO base media files, as the MP4 registration authority lists it).";
    public const string ChaptersReserved = "Four bytes that version 1 of Nero's chapter box adds, as ffmpeg reads it.";
    public const string ChapterCount = "The number of chapters in Nero's list.";
    public const string Chapter = "A chapter of Nero's list: its start and title.";
    public const string ChapterStart = "Where the chapter starts, stored in units of 100 nanoseconds.";
    public const string ChapterTitleLength = "The length of the chapter's title in bytes.";
    public const string ChapterTitle = "The chapter's title.";
    public const string TextLength = "The length of the text in bytes (QuickTime File Format, User data text).";
    public const string TextLanguage = "The text's language, a Macintosh language code or packed ISO 639-2/T letters (QuickTime File Format, Language code values).";
    public const string Text = "The text of QuickTime user data, such as the name of the encoding software (QuickTime File Format, User data atoms).";

    public const string FragmentDuration = "The length of the whole fragmented presentation, in the movie's timescale (ISO/IEC 14496-12 8.8.2).";
    public const string TrexTrackId = "The track whose fragment defaults these are (ISO/IEC 14496-12 8.8.3).";
    public const string TrexDescriptionIndex = "The sample description fragments of this track use unless they say otherwise (ISO/IEC 14496-12 8.8.3).";
    public const string TrexDuration = "The sample duration fragments of this track use unless they say otherwise, in the media's timescale (ISO/IEC 14496-12 8.8.3).";
    public const string TrexSize = "The sample size fragments of this track use unless they say otherwise (ISO/IEC 14496-12 8.8.3).";
    public const string SampleFlags = "Sample flags: whether the sample depends on others, whether others depend on it, its padding, whether it is a sync sample, and its degradation priority (ISO/IEC 14496-12 8.8.3.1).";
    public const string SequenceNumber = "The fragment's number, which increases from one fragment to the next (ISO/IEC 14496-12 8.8.5).";
    public const string TfhdTrackId = "The track this track fragment adds samples to (ISO/IEC 14496-12 8.8.7).";
    public const string BaseDataOffset = "The file offset from which the fragment's data offsets count (ISO/IEC 14496-12 8.8.7).";
    public const string TfhdDescriptionIndex = "The sample description this fragment's samples use (ISO/IEC 14496-12 8.8.7).";
    public const string TfhdDuration = "The duration of this fragment's samples unless a run lists them, in the media's timescale (ISO/IEC 14496-12 8.8.7).";
    public const string TfhdSize = "The size of this fragment's samples unless a run lists them (ISO/IEC 14496-12 8.8.7).";
    public const string BaseMediaDecodeTime = "The decoding time of the fragment's first sample, in the media's timescale: the duration of all samples before it (ISO/IEC 14496-12 8.8.12).";
    public const string RunSamples = "The number of samples in this run (ISO/IEC 14496-12 8.8.8).";
    public const string DataOffset = "Where the run's first sample starts, counted from the fragment's base offset (ISO/IEC 14496-12 8.8.8).";
    public const string FirstSampleFlags = "The flags of the run's first sample, which often differ from the rest, as for a sync sample (ISO/IEC 14496-12 8.8.8).";
    public const string RunSample = "One sample of the run, with the fields the run's flags select (ISO/IEC 14496-12 8.8.8).";
    public const string RunSampleDuration = "How long the sample lasts, in the media's timescale (ISO/IEC 14496-12 8.8.8).";
    public const string RunSampleSize = "The sample's size in bytes (ISO/IEC 14496-12 8.8.8).";
    public const string RunSampleFlags = "The sample's flags: its dependencies and whether it is a sync sample (ISO/IEC 14496-12 8.8.8).";
    public const string RunCompositionOffset = "How much later than its decoding time the sample is presented (ISO/IEC 14496-12 8.8.8).";
    public const string ReferenceId = "The stream this segment index describes (ISO/IEC 14496-12 8.16.3).";
    public const string SidxTimescale = "The time units per second of the segment index's times (ISO/IEC 14496-12 8.16.3).";
    public const string EarliestPresentationTime = "The presentation time of the first sample the index covers (ISO/IEC 14496-12 8.16.3).";
    public const string FirstOffset = "The distance from the end of this box to the first referenced subsegment (ISO/IEC 14496-12 8.16.3).";
    public const string ReferenceCount = "The number of subsegments the index lists (ISO/IEC 14496-12 8.16.3).";
    public const string Reference = "One subsegment: its size, duration and where a player can start in it (ISO/IEC 14496-12 8.16.3).";
    public const string ReferenceType = "Whether the reference points to media (0) or to another segment index (1) (ISO/IEC 14496-12 8.16.3).";
    public const string ReferencedSize = "The size of the referenced subsegment in bytes (ISO/IEC 14496-12 8.16.3).";
    public const string SubsegmentDuration = "The length of the referenced subsegment (ISO/IEC 14496-12 8.16.3).";
    public const string StartsWithSap = "Whether the subsegment starts with a stream access point, where decoding can start (ISO/IEC 14496-12 8.16.3).";
    public const string SapType = "The kind of stream access point, 1 to 6, 0 where unknown (ISO/IEC 14496-12 8.16.3).";
    public const string SapDeltaTime = "How far into the subsegment its first stream access point lies (ISO/IEC 14496-12 8.16.3).";
    public const string TfraTrackId = "The track whose random access points the table lists (ISO/IEC 14496-12 8.8.10).";
    public const string LengthSizes = "The sizes of the track fragment, run and sample numbers that follow in each entry, coded in two bits each (ISO/IEC 14496-12 8.8.10).";
    public const string TfraEntries = "The number of random access points listed (ISO/IEC 14496-12 8.8.10).";
    public const string TfraEntry = "A sample a player can start at: its time and where its fragment is (ISO/IEC 14496-12 8.8.10).";
    public const string TfraTime = "The presentation time of the random access sample (ISO/IEC 14496-12 8.8.10).";
    public const string MoofOffset = "Where the moof box that holds the sample starts in the file (ISO/IEC 14496-12 8.8.10).";
    public const string TrafNumber = "Which track fragment in that moof box holds the sample, counted from 1 (ISO/IEC 14496-12 8.8.10).";
    public const string TrunNumber = "Which track run in that track fragment holds the sample, counted from 1 (ISO/IEC 14496-12 8.8.10).";
    public const string TfraSampleNumber = "Which sample of that run it is, counted from 1 (ISO/IEC 14496-12 8.8.10).";
    public const string MfroSize = "The size of the enclosing mfra box, so a reader can find it from the end of the file (ISO/IEC 14496-12 8.8.11).";

    public const string TrackSamples = "The bytes of one track's samples, which the sample tables place in the media data; the codec's nodes under it show each access unit.";
    public const string Gapless = "What the file says about the samples to trim for gapless playback: the edit list, the roll groups and Apple's iTunSMPB. Counted in the track's media timescale.";
    public const string EditDelay = "The samples the edit list skips at the start: its media time, the encoder delay a player trims (ISO/IEC 14496-12 8.6.6).";
    public const string EditSamples = "The samples the edit list presents: its segment duration, converted to the media's timescale.";
    public const string EditPadding = "The samples left after the edit list's end: the padding in the last access units, which a player trims.";
    public const string GaplessRoll = "The roll distance the track's roll group gives: how many access units a decoder needs before the one it plays (ISO/IEC 14496-12 10.1).";
    public const string GaplessPreRoll = "The pre-roll distance the track's pre-roll group gives: how many access units a decoder needs before it reaches full quality (ISO/IEC 14496-12, audio pre-roll).";
    public const string SmpbGaplessDelay = "The encoder delay that Apple's iTunSMPB states for the track.";
    public const string SmpbGaplessSamples = "The source's length in samples that Apple's iTunSMPB states.";
    public const string SmpbGaplessPadding = "The padding that Apple's iTunSMPB states for the track.";

    public const string UnknownBox = "A box the analyzer does not know; its type is a four-character code registered by a specification or chosen by a writer.";
    public const string UnknownItem = "An iTunes metadata item the analyzer does not name; its type is a four-character code, and it holds one or more data atoms (Apple iTunes metadata).";

    // ISO/IEC 14496-12, 14496-14, the QuickTime File Format and Apple's iTunes metadata, by box type.
    public static string Box(string type) => type switch
    {
        "ftyp" => "File type box: the brand of the specification the file follows, its version and the other brands it is compatible with, so a reader can tell whether it can play the file (ISO/IEC 14496-12 4.3).",
        "styp" => "Segment type box: the brands of a media segment that is sent on its own, in place of ftyp (ISO/IEC 14496-12 8.16.2).",
        "moov" => "Movie box: the description of the whole presentation, its header, tracks and metadata. It holds no media itself; its sample tables point into the media data (ISO/IEC 14496-12 8.2.1).",
        "mvhd" => "Movie header: when the movie was made, the timescale its durations count in, the duration of its longest track, and its playback rate and volume (ISO/IEC 14496-12 8.2.2).",
        "trak" => "Track box: one stream of the presentation, such as an audio track, with its header, edits and media (ISO/IEC 14496-12 8.3.1).",
        "tkhd" => "Track header: the track's ID, its duration in the movie's timescale, whether it is enabled, its volume and, for video, its size (ISO/IEC 14496-12 8.3.2).",
        "tref" => "Track reference box: the other tracks this track refers to, such as a chapter track (ISO/IEC 14496-12 8.3.3).",
        "edts" => "Edit box: holds the edit list, which says which part of the media the track presents (ISO/IEC 14496-12 8.6.5).",
        "elst" => "Edit list: maps the track's presentation to stretches of its media, which lets a player skip an encoder's priming samples and the padding at the end (ISO/IEC 14496-12 8.6.6).",
        "mdia" => "Media box: the track's media, with its header, its handler and where its samples are (ISO/IEC 14496-12 8.4.1).",
        "mdhd" => "Media header: the media's own timescale, for audio usually the sample rate, its duration in that timescale and its language (ISO/IEC 14496-12 8.4.2).",
        "hdlr" => "Handler reference: the kind of media a track holds, such as sound, or in a meta box the format its metadata follows (ISO/IEC 14496-12 8.4.3).",
        "minf" => "Media information box: the header of the track's kind of media, the data references and the sample table (ISO/IEC 14496-12 8.4.4).",
        "smhd" => "Sound media header: the audio track's balance between the speakers (ISO/IEC 14496-12 12.2.2).",
        "vmhd" => "Video media header: how a video track is drawn (ISO/IEC 14496-12 12.1.2).",
        "nmhd" => "Null media header: the media header of a track that is neither audio nor video (ISO/IEC 14496-12 8.4.5.2).",
        "gmhd" => "QuickTime's base media information header, for tracks such as text, chapters or timecode (QuickTime File Format, Base media information atom).",
        "dinf" => "Data information box: holds the data references that say where the track's media data is (ISO/IEC 14496-12 8.7.1).",
        "dref" => "Data reference box: the places the track's media data can be, this file or another one (ISO/IEC 14496-12 8.7.2).",
        "url " => "Data entry URL: where the media data is; flag 1 means in this same file (ISO/IEC 14496-12 8.7.2).",
        "urn " => "Data entry URN: a name and location of the media data (ISO/IEC 14496-12 8.7.2).",
        "stbl" => "Sample table: what is needed to find and time the track's samples: their descriptions, durations, sizes, chunks and chunk offsets (ISO/IEC 14496-12 8.5.1).",
        "stsd" => "Sample description box: how the samples are coded, as one or more sample entries such as mp4a for MPEG-4 audio (ISO/IEC 14496-12 8.5.2).",
        "stts" => "Decoding time to sample: runs of samples with the same duration, from which each sample's decoding time follows (ISO/IEC 14496-12 8.6.1.2).",
        "ctts" => "Composition time to sample: how much later than its decoding time each sample is presented (ISO/IEC 14496-12 8.6.1.3).",
        "stsc" => "Sample to chunk: how many samples each chunk holds and which description they use (ISO/IEC 14496-12 8.7.4).",
        "stsz" => "Sample size box: the size of every sample, one for all or one each (ISO/IEC 14496-12 8.7.3.2).",
        "stz2" => "Compact sample size box: the samples' sizes in 4, 8 or 16 bits each (ISO/IEC 14496-12 8.7.3.3).",
        "stco" => "Chunk offset box: where each chunk starts in the file, in 32 bits (ISO/IEC 14496-12 8.7.5).",
        "co64" => "Chunk offset box with 64-bit offsets, for files of 4 GiB or more (ISO/IEC 14496-12 8.7.5).",
        "stss" => "Sync sample box: the samples a decoder can start at. Without it every sample is one (ISO/IEC 14496-12 8.6.2).",
        "sdtp" => "Independent and disposable samples box: which samples depend on others (ISO/IEC 14496-12 8.6.4).",
        "sgpd" => "Sample group description: the properties a group of samples shares, such as the roll distance of AAC access units (ISO/IEC 14496-12 8.9.3).",
        "sbgp" => "Sample to group: which samples belong to which group description (ISO/IEC 14496-12 8.9.2).",
        "csgp" => "Compact sample to group: a compact form of the sample-to-group table (ISO/IEC 14496-12).",
        "esds" => "Elementary stream descriptor box: the MPEG-4 descriptors of the stream, with the decoder's configuration, for AAC the AudioSpecificConfig (ISO/IEC 14496-14 5.6).",
        "sbtd" => "Source bit depth box: a box in the audio sample entries of afconvert's M4A files, with the bit depth of the audio that was encoded as text (\"I16\" for 16-bit integers). It is not part of ISO/IEC 14496-12 or 14496-14.",
        "btrt" => "Bitrate box: the decoder's buffer size and the stream's maximum and average bitrate (ISO/IEC 14496-12 8.5.2.2).",
        "chan" => "QuickTime channel layout: which speaker each channel feeds, as Core Audio's AudioChannelLayout: a layout tag, a channel bitmap or a description per channel (QuickTime File Format, Audio channel layout atom).",
        "chnl" => "Channel layout box: whether the track carries channels or objects, and which speaker each channel feeds, as a channel configuration of ISO/IEC 23091-3 or a speaker position per channel (ISO/IEC 14496-12 12.2.4).",
        "wave" => "QuickTime sound decompression parameters: extensions of a sound sample description, such as an original format and an esds box (QuickTime File Format, Sound sample description extensions).",
        "frma" => "Original format box: the format of a sample entry that is wrapped, as in QuickTime's wave atom or a protected entry (ISO/IEC 14496-12 8.12.2).",
        "mvex" => "Movie extends box: says the file has movie fragments after moov, and gives the tracks' defaults for them (ISO/IEC 14496-12 8.8.1).",
        "mehd" => "Movie extends header: the length of the whole fragmented presentation (ISO/IEC 14496-12 8.8.2).",
        "trex" => "Track extends box: a track's defaults for the samples of its fragments (ISO/IEC 14496-12 8.8.3).",
        "moof" => "Movie fragment box: the sample tables of the next stretch of the presentation, whose samples follow in an mdat box (ISO/IEC 14496-12 8.8.4).",
        "mfhd" => "Movie fragment header: the fragment's sequence number (ISO/IEC 14496-12 8.8.5).",
        "traf" => "Track fragment box: the samples one track adds in this fragment (ISO/IEC 14496-12 8.8.6).",
        "tfhd" => "Track fragment header: the track, the base the data offsets count from, and the defaults for the fragment's samples (ISO/IEC 14496-12 8.8.7).",
        "tfdt" => "Track fragment decode time: the decoding time of the fragment's first sample (ISO/IEC 14496-12 8.8.12).",
        "trun" => "Track fragment run: a run of samples stored back to back, with each one's size and duration where the defaults do not hold (ISO/IEC 14496-12 8.8.8).",
        "sidx" => "Segment index: the subsegments of a stream with their sizes and durations, for streaming (ISO/IEC 14496-12 8.16.3).",
        "mfra" => "Movie fragment random access box: where in the fragments a player can start, at the end of the file (ISO/IEC 14496-12 8.8.9).",
        "tfra" => "Track fragment random access box: a track's samples a player can start at, with their times and fragments (ISO/IEC 14496-12 8.8.10).",
        "mfro" => "Movie fragment random access offset: the size of the mfra box, so a reader finds it from the end of the file (ISO/IEC 14496-12 8.8.11).",
        "mdat" => "Media data box: the coded samples of the tracks, which the sample tables locate (ISO/IEC 14496-12 8.1.1).",
        "free" or "skip" => "Free space box: bytes a reader ignores, often room left for moov to grow (ISO/IEC 14496-12 8.1.2).",
        "wide" => "QuickTime placeholder: eight bytes that the next box's header can take over when it needs a 64-bit size (QuickTime File Format, wide atom).",
        "udta" => "User data box: information about the movie or track that players do not need, such as metadata (ISO/IEC 14496-12 8.10.1).",
        "meta" => "Metadata box: metadata whose format its handler names, such as Apple's iTunes metadata (ISO/IEC 14496-12 8.11.1).",
        "keys" => "Metadata keys: the names of the QuickTime metadata items, which items refer to by index (QuickTime File Format, Metadata item keys atom).",
        "ilst" => "Metadata item list: the file's tags, one box per item, each with its value in a data atom (QuickTime File Format, Metadata item list atom; Apple iTunes metadata).",
        "data" => "Data atom: one value of a metadata item, with its type and locale (QuickTime File Format, Data atom).",
        "mean" => "Mean atom: the reverse-DNS domain of a freeform metadata item (Apple iTunes metadata).",
        "name" => "Name atom: the name of a freeform metadata item (Apple iTunes metadata).",
        "ID32" => "ID3v2 box: a whole ID3v2 tag stored in a meta box, with its language (ID3v2 in ISO base media files, as the MP4 registration authority lists it).",
        "chpl" => "Nero's chapter list: chapter start times and titles, as Nero's tools write them.",
        "iods" => "Initial object descriptor: the MPEG-4 systems description of the presentation, mostly profile levels (ISO/IEC 14496-14 5.1).",
        "uuid" => "Extension box: a box of a type that a UUID names instead of a registered four-character code (ISO/IEC 14496-12 4.2).",
        "schm" => "Scheme type box: the protection scheme, a four-character code such as cenc, its version and optionally a URI (ISO/IEC 14496-12 8.12.5).",
        "tenc" => "Track encryption box: the track's default encryption parameters: whether its samples are protected, the size of their initialization vectors, the default key ID and, where samples carry no vector, a constant one (ISO/IEC 23001-7).",
        "pssh" => "Protection system specific header box: the data one DRM system needs to get the keys, named by the system's ID (ISO/IEC 23001-7).",
        "senc" => "Sample encryption box: the initialization vector and subsample ranges of each sample of a track or track fragment (ISO/IEC 23001-7).",
        "saiz" => "Sample auxiliary information sizes: the size of each sample's auxiliary information, which for encrypted tracks is the sample's initialization vector and subsample ranges (ISO/IEC 14496-12 8.7.8; ISO/IEC 23001-7).",
        "saio" => "Sample auxiliary information offsets: where the auxiliary information of the samples lies (ISO/IEC 14496-12 8.7.9; ISO/IEC 23001-7).",
        "adrm" => "Audible's DRM box in an aavd sample entry: a blob that FFmpeg decrypts with the account's activation bytes, and a checksum (FFmpeg's libavformat/mov.c, mov_read_adrm).",
        "user" or "key " or "iviv" or "priv" => "A box of Apple's iTunes Store protection (FairPlay) in the scheme information of a drms entry; VLC's libmp4.c lists it as one of the DRM atoms and skips it, and its content is not described by any source the analyzer follows.",
        "enca" => "Encrypted audio sample entry: audio whose samples are protected; its sinf box names the original format and the scheme (ISO/IEC 14496-12 8.12).",
        "encv" => "Encrypted video sample entry: video whose samples are protected; its sinf box names the original format and the scheme (ISO/IEC 14496-12 8.12).",
        "drms" => "Apple's iTunes Store protected audio sample entry (FairPlay), laid out like the original entry, with the original format in its sinf box; MediaInfo reports its encryption as iTunes and VLC does not play it.",
        "drmi" => "Apple's iTunes Store protected video sample entry (FairPlay); VLC's libmp4.c reads it as protected.",
        "aavd" => "Audible's protected audio sample entry, laid out like mp4a with an esds box and an adrm box (GStreamer's qtdemux reads it so).",
        "sinf" => "Protection scheme information: how a protected track's samples are encrypted and what their original format was (ISO/IEC 14496-12 8.12.1).",
        "schi" => "Scheme information box: the data of a protection scheme (ISO/IEC 14496-12 8.12).",
        "pdin" => "Progressive download information: download rates and the initial delays they need for playback (ISO/IEC 14496-12 8.1.3).",
        "mp4a" => "MPEG-4 audio sample entry: audio coded by an MPEG audio codec, usually AAC, whose configuration its esds box holds (ISO/IEC 14496-14 5.6).",
        "ac-3" => "AC-3 sample entry: Dolby Digital audio, one syncframe per sample, configured by its dac3 box (ETSI TS 102 366 F.3).",
        "ec-3" => "E-AC-3 sample entry: Dolby Digital Plus audio, the syncframes of six blocks of every substream per sample, configured by its dec3 box (ETSI TS 102 366 F.5).",
        "dac3" => "AC3SpecificBox: the AC-3 stream's sample rate, version, service, channels and bitrate, as its frames hold them (ETSI TS 102 366 F.4).",
        "dec3" => "EC3SpecificBox: the E-AC-3 stream's data rate and, per independent substream, its sample rate, version, service, channels and dependent substreams (ETSI TS 102 366 F.6).",
        "dtsc" => "DTS sample entry: DTS audio of the formats before DTS-HD, one core frame per sample, configured by its ddts box (ETSI TS 102 114 E.2.2).",
        "dtsh" => "DTS-HD sample entry: DTS audio with extension substreams, a core frame and its extension substream per sample, configured by its ddts box (ETSI TS 102 114 E.2.2).",
        "dtsl" => "DTS-HD lossless sample entry: DTS audio with a lossless extension, configured by its ddts box (ETSI TS 102 114 E.2.2).",
        "dtse" => "DTS low bit rate sample entry: DTS Express audio, configured by its ddts box (ETSI TS 102 114 E.2.2).",
        "ddts" => "DTSSpecificBox: the DTS stream's sampling frequency, bit rates, bit depth, frame duration, stream construction, core layout and size, and channel layout, as its frames hold them (ETSI TS 102 114 E.2.2.3).",
        ".mp3" => "QuickTime's MP3 sample entry: MPEG-1 or MPEG-2 Layer III audio (QuickTime File Format, Sound sample descriptions).",
        _ when type.StartsWith('©') => "QuickTime user data text, such as a name or the encoding software, with a length and language before the text (QuickTime File Format, User data atoms).",
        _ => UnknownBox,
    };

    // A sample entry of an stsd box, by its coding.
    public static string SampleEntry(string type) => type switch
    {
        "mp4a" or ".mp3" or "ac-3" or "ec-3" or "dtsc" or "dtsh" or "dtsl" or "dtse" or "enca" or "encv" or "drms" or "drmi" or "aavd" => Box(type),
        _ => "A sample entry: how the track's samples are coded, named by the four-character code of the coding, such as alac or Opus (ISO/IEC 14496-12 8.5.2.2).",
    };

    public static string? BoxTopic(string type) => type switch
    {
        "ftyp" or "styp" or "moov" or "mvhd" or "trak" or "tkhd" or "mdia" or "mdhd" or "hdlr" or "minf" or "mdat" or "free" or "skip" or "wide" or "udta" or "iods" or "uuid" => Topics.IsoBaseMediaFormat,
        "stbl" or "stsd" or "stts" or "ctts" or "stsc" or "stsz" or "stz2" or "stco" or "co64" or "stss" or "sdtp" or "dinf" or "dref" or "url " or "urn " or "smhd" => Topics.SampleTables,
        "edts" or "elst" or "sgpd" or "sbgp" or "csgp" => Topics.EditListsAndPriming,
        "mvex" or "mehd" or "trex" or "moof" or "mfhd" or "traf" or "tfhd" or "tfdt" or "trun" or "sidx" or "mfra" or "tfra" or "mfro" => Topics.FragmentedMp4,
        "meta" or "keys" or "ilst" or "data" or "mean" or "name" or "chpl" => Topics.ItunesMetadata,
        "sinf" or "schm" or "schi" or "tenc" or "pssh" or "senc" or "saiz" or "saio" or "enca" or "encv" => Topics.CommonEncryption,
        "drms" or "drmi" or "aavd" or "adrm" or "user" or "key " or "iviv" or "priv" => Topics.FairPlayAndAudible,
        "esds" => Topics.AudioSpecificConfig,
        "btrt" => Topics.BitrateModes,
        "ID32" => Topics.Id3v2,
        "chan" or "chnl" => Topics.ChannelLayouts,
        _ => null,
    };

    // Apple's iTunes metadata items, by their type, as TagLib, ffmpeg and MP4Box read them.
    public static string Item(string type) => type switch
    {
        "©nam" => "The title of the track (iTunes metadata ©nam).",
        "©ART" => "The artist of the track (iTunes metadata ©ART).",
        "aART" => "The artist of the album as a whole (iTunes metadata aART).",
        "©alb" => "The album the track belongs to (iTunes metadata ©alb).",
        "©gen" => "The genre as free text (iTunes metadata ©gen).",
        "gnre" => "The genre as a number: one more than its ID3v1 genre number (iTunes metadata gnre).",
        "©day" => "The release date or year (iTunes metadata ©day).",
        "trkn" => "The track number and the number of tracks, stored as 16-bit numbers in binary data (iTunes metadata trkn).",
        "disk" => "The disc number and the number of discs, stored as 16-bit numbers in binary data (iTunes metadata disk).",
        "©wrt" => "The composer (iTunes metadata ©wrt).",
        "©cmt" => "A comment (iTunes metadata ©cmt).",
        "©too" => "The software that encoded the file, as it names itself. Software that only rewrites a file may also set it (iTunes metadata ©too).",
        "©enc" => "Who or what encoded the file (iTunes metadata ©enc).",
        "cprt" => "The copyright notice (iTunes metadata cprt).",
        "©grp" => "The grouping, a label that ties tracks together (iTunes metadata ©grp).",
        "©lyr" => "The lyrics (iTunes metadata ©lyr).",
        "covr" => "Cover art, one image per data atom, in JPEG, PNG or BMP (iTunes metadata covr).",
        "tmpo" => "The tempo in beats per minute (iTunes metadata tmpo).",
        "cpil" => "Whether the track is part of a compilation (iTunes metadata cpil).",
        "pgap" => "Whether the track belongs to a gapless album, which iTunes plays without pauses between tracks (iTunes metadata pgap).",
        "stik" => "The kind of media, such as music, audiobook or podcast, as a number (iTunes metadata stik).",
        "rtng" => "The content rating, such as explicit or clean, as a number (iTunes metadata rtng).",
        "desc" or "©des" => "A short description (iTunes metadata desc).",
        "ldes" => "A long description (iTunes metadata ldes).",
        "sonm" => "The title as it sorts (iTunes metadata sonm).",
        "soar" => "The artist as it sorts (iTunes metadata soar).",
        "soaa" => "The album artist as it sorts (iTunes metadata soaa).",
        "soal" => "The album as it sorts (iTunes metadata soal).",
        "soco" => "The composer as it sorts (iTunes metadata soco).",
        "sosn" => "The show as it sorts (iTunes metadata sosn).",
        "tvsh" => "The TV show (iTunes metadata tvsh).",
        "purd" => "When the track was bought (iTunes metadata purd).",
        "apID" => "The account that bought the track (iTunes metadata apID).",
        "ownr" => "The owner of the track (iTunes metadata ownr).",
        "cnID" => "The iTunes Store catalog ID of the track (iTunes metadata cnID).",
        "atID" => "The iTunes Store ID of the artist (iTunes metadata atID).",
        "plID" => "The iTunes Store ID of the album (iTunes metadata plID).",
        "geID" => "The iTunes Store ID of the genre (iTunes metadata geID).",
        "sfID" => "The iTunes Store country the track was bought in (iTunes metadata sfID).",
        "akID" => "The kind of iTunes Store account (iTunes metadata akID).",
        "hdvd" => "Whether the video is high definition (iTunes metadata hdvd).",
        "pcst" => "Whether the track is a podcast (iTunes metadata pcst).",
        "catg" => "The podcast's category (iTunes metadata catg).",
        "keyw" => "The podcast's keywords (iTunes metadata keyw).",
        "purl" => "The podcast's feed URL (iTunes metadata purl).",
        "egid" => "The podcast episode's GUID (iTunes metadata egid).",
        "©wrk" => "The classical work the track is part of (iTunes metadata ©wrk).",
        "©mvn" => "The movement's name (iTunes metadata ©mvn).",
        "©mvi" => "The movement's number (iTunes metadata ©mvi).",
        "©mvc" => "The number of movements in the work (iTunes metadata ©mvc).",
        "shwm" => "Whether to show the work and movement instead of the title (iTunes metadata shwm).",
        "©st3" => "The subtitle (iTunes metadata ©st3).",
        "----" => "A freeform item: a value named by a domain (mean) and a name, such as Apple's iTunSMPB with the encoder delay and padding (Apple iTunes metadata).",
        _ => UnknownItem,
    };

    // A freeform item, by its name, where the name says more than the freeform structure.
    public static string Freeform(string? name) => name switch
    {
        "iTunSMPB" => "Apple's gapless information: hexadecimal words with the encoder delay, the padding and the source's length in samples (Apple iTunes metadata, iTunSMPB).",
        "iTunNORM" => "Apple's Sound Check values, the loudness iTunes measured to play tracks at an even level (Apple iTunes metadata, iTunNORM).",
        "Encoding Params" => "The settings of Apple's encoder: version, bitrate control mode, bitrate and codec version, as key and value pairs (Apple iTunes metadata).",
        _ => Item("----"),
    };
}

namespace Encspect.Formats.Vorbis;

// The background articles the Vorbis nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string VorbisBlocks = "vorbis-blocks";
    public const string VorbisSetupHeader = "vorbis-setup-header";
    public const string SquarePolarCoupling = "square-polar-coupling";
    public const string GranulePosition = "granule-position";
    public const string BitrateModes = "bitrate-modes";
    public const string EncoderDetection = "encoder-detection";
}

// What each Vorbis node is. Constants only, shared by every node of their kind, from the Vorbis I
// specification: section 4.2 for the headers, 3.2.1 for codebooks, 6.2.1 and 7.2.2 for floors,
// 8.6.1 for residues, 4.3 for audio packets and A.2 for granule positions in Ogg.
internal static class Explain
{
    public const string Identification = "The identification header, the stream's first packet: Vorbis version, channels, sample rate, bitrate hints and the two block sizes (Vorbis I, section 4.2.2).";
    public const string IdentificationShort = "A first packet too short to hold the 30-byte identification header (Vorbis I, section 4.2.2).";
    public const string PacketTypeIdentification = "The header packet type, 1 for the identification header (Vorbis I, section 4.2.1).";
    public const string PacketTypeSetup = "The header packet type, 5 for the setup header (Vorbis I, section 4.2.1).";
    public const string VorbisString = "The string \"vorbis\", which every header packet carries after its type (Vorbis I, section 4.2.1).";
    public const string Version = "The Vorbis version, 0 for Vorbis I; decoders refuse any other (vorbis_version, section 4.2.2).";
    public const string Channels = "The number of audio channels, in the order section 4.3.9 gives for one to eight (audio_channels).";
    public const string ChannelLayout = "The speaker of each channel, in order, which the channel count gives for one to eight channels (Vorbis I, section 4.3.9).";
    public const string SampleRate = "The sample rate of the audio in Hz (audio_sample_rate, section 4.2.2).";
    public const string BitrateMaximum = "The bitrate the encoder promises not to exceed, in bits per second; a hint, meaningful only above 0 (bitrate_maximum, section 4.2.2).";
    public const string BitrateNominal = "The average bitrate the encoder aimed at, in bits per second; a hint, meaningful only above 0 (bitrate_nominal, section 4.2.2).";
    public const string BitrateMinimum = "The bitrate the encoder promises not to fall below, in bits per second; a hint, meaningful only above 0 (bitrate_minimum, section 4.2.2).";
    public const string BitrateHint = "What the three bitrate fields together say, as section 4.2.2 reads them: all equal for a nearly fixed rate, only the nominal for VBR or ABR, a maximum or minimum for bounded VBR, none for no hint.";
    public const string Blocksize0 = "The short block size, a power of two from 64 to 8192 samples, stored as its exponent in four bits (blocksize_0, section 4.2.2).";
    public const string Blocksize1 = "The long block size, a power of two from 64 to 8192 samples and not smaller than the short one, stored as its exponent (blocksize_1, section 4.2.2).";
    public const string FramingBit = "The framing bit, which must be 1; libvorbis refuses a header without it (section 4.2.2).";
    public const string IdentificationExtraData = "Bytes in the identification header packet after its framing bit, which decoders ignore.";
    public const string CommentPrefix = "The comment header's packet type 3 and \"vorbis\", before the Vorbis comment (Vorbis I, section 5.2.1).";
    public const string CommentTrailingData = "Bytes in the comment header packet after its framing bit, which readers ignore.";
    public const string UnexpectedPacket = "A packet where the Vorbis I specification puts a header, which is not the header expected there (section 4.2.1).";

    public const string Setup = "The setup header, the third header packet: the codebooks, floors, residues, mappings and modes the audio packets are decoded with, packed least significant bit first (Vorbis I, section 4.2.4).";
    public const string SetupTrailingData = "Bytes in the setup header packet after its framing bit, which decoders ignore.";
    public const string Codebooks = "The codebooks: Huffman codes, some with vector lookup tables, that the floors and residues decode their values with (Vorbis I, sections 3.2.1 and 4.2.4).";
    public const string CodebookCount = "How many codebooks follow, 1 to 256 (vorbis_codebook_count, section 4.2.4).";
    public const string Codebook = "A codebook: its dimensions and entries, the length of each entry's codeword, and an optional lookup table that turns an entry into a vector (Vorbis I, section 3.2.1).";
    public const string SyncPattern = "The pattern 0x564342 (\"BCV\") that starts every codebook (section 3.2.1).";
    public const string Dimensions = "How many values each entry's vector holds (codebook_dimensions, section 3.2.1).";
    public const string Entries = "How many entries, and so codewords, the codebook has (codebook_entries, section 3.2.1).";
    public const string IsOrdered = "Whether the codeword lengths are stored in ascending order as runs of entries per length, rather than one length per entry (ordered, section 3.2.1).";
    public const string IsSparse = "Whether some entries are unused, in which case a flag before each entry says whether it has a codeword (sparse, section 3.2.1).";
    public const string CodewordLengths = "The length of each entry's codeword in bits, 1 to 32, from which the decoder builds the Huffman code (section 3.2.1).";
    public const string CodewordLengthsOrdered = "The codeword lengths as runs: a first length, then how many entries have each length from there on up (section 3.2.1).";
    public const string LookupType = "How entries map to vectors: 0 none (scalar use only), 1 a lattice built from a small value list, 2 one stored value per entry and dimension (codebook_lookup_type, section 3.2.1).";
    public const string MinimumValue = "The smallest value of the lookup table, a packed float (codebook_minimum_value, sections 3.2.1 and 9.2.2).";
    public const string DeltaValue = "The step between the lookup table's values, a packed float (codebook_delta_value, sections 3.2.1 and 9.2.2).";
    public const string ValueBits = "How many bits each stored multiplicand takes (codebook_value_bits, section 3.2.1).";
    public const string IsSequence = "Whether each vector's values accumulate, each adding the previous one (codebook_sequence_p, section 3.2.1).";
    public const string Multiplicands = "The lookup table's stored values, which are not shown one by one (codebook_multiplicands, section 3.2.1).";
    public const string Times = "Placeholders for time domain transforms, which Vorbis I does not use; each must be 0 (section 4.2.4).";
    public const string TimeCount = "How many time domain placeholders follow (vorbis_time_count, section 4.2.4).";
    public const string TimeType = "A time domain placeholder, which must be 0 (section 4.2.4).";
    public const string Floors = "The floor configurations: how a packet codes each channel's spectral envelope, the coarse curve its residue is scaled by (Vorbis I, section 4.2.4).";
    public const string FloorCount = "How many floor configurations follow (vorbis_floor_count, section 4.2.4).";
    public const string Floor = "A floor configuration, of type 0 (line spectral pairs) or type 1 (a piecewise linear curve) (sections 6 and 7).";
    public const string FloorType = "The floor type: 0 codes the envelope as line spectral pairs (section 6), 1 as a piecewise linear curve on a list of X positions (section 7).";
    public const string Floor0Order = "The order of the line spectral pair filter (floor0_order, section 6.2.1).";
    public const string Floor0Rate = "The sample rate the floor's curve is computed at (floor0_rate, section 6.2.1).";
    public const string Floor0BarkMapSize = "How many points of the Bark scale the floor's curve is computed on (floor0_bark_map_size, section 6.2.1).";
    public const string Floor0AmplitudeBits = "How many bits code the floor's amplitude (floor0_amplitude_bits, section 6.2.1).";
    public const string Floor0AmplitudeOffset = "The amplitude offset of the floor, in dB (floor0_amplitude_offset, section 6.2.1).";
    public const string Floor0Books = "How many codebooks the floor may decode its coefficients with (floor0_number_of_books, section 6.2.1).";
    public const string FloorBooks = "The codebooks the floor decodes its coefficients with (floor0_book_list, section 6.2.1).";
    public const string Floor0Book = "A codebook number (section 6.2.1).";
    public const string Floor1Partitions = "How many partitions the curve's X positions are split into (floor1_partitions, section 7.2.2).";
    public const string Floor1PartitionClasses = "The class of each partition, 0 to 15 (floor1_partition_class_list, section 7.2.2).";
    public const string Floor1Classes = "The partition classes: how many Y values each partition of the class codes, and with which codebooks (section 7.2.2).";
    public const string Floor1Class = "A partition class: its dimensions, its subclass bits and their master codebook, and the codebook of each subclass (section 7.2.2).";
    public const string Floor1ClassDimensions = "How many X positions a partition of this class holds, 1 to 8 (floor1_class_dimensions, section 7.2.2).";
    public const string Floor1ClassSubclasses = "How many bits select a subclass, 0 to 3 (floor1_class_subclasses, section 7.2.2).";
    public const string Floor1Masterbook = "The codebook that codes the subclass choices (floor1_class_masterbooks, section 7.2.2).";
    public const string Floor1SubclassBook = "The codebook of one subclass, stored plus one; 0 means its values are not coded (floor1_subclass_books, section 7.2.2).";
    public const string Floor1Multiplier = "The step the curve's Y values are scaled by, 1 to 4 (floor1_multiplier, section 7.2.2).";
    public const string Floor1RangeBits = "How many bits each X position takes; the curve spans 0 to 2^rangebits (rangebits, section 7.2.2).";
    public const string Floor1XList = "The X positions of the curve's points beyond the two ends, 0 and 2^rangebits (floor1_X_list, section 7.2.2).";
    public const string Residues = "The residue configurations: how a packet codes the fine spectral detail left after the floor, by vector quantization in passes (Vorbis I, section 4.2.4).";
    public const string ResidueCount = "How many residue configurations follow (vorbis_residue_count, section 4.2.4).";
    public const string Residue = "A residue configuration: the range of coefficients it codes, its partition size, its classifications and the codebooks of each pass (section 8.6.1).";
    public const string ResidueType = "The residue type: 0 and 1 code each channel's vector apart, interleaved or in order; 2 codes the channels interleaved into one vector (section 8).";
    public const string ResidueBegin = "The first coefficient the residue codes (residue_begin, section 8.6.1).";
    public const string ResidueEnd = "Where the coefficients the residue codes end (residue_end, section 8.6.1).";
    public const string ResiduePartitionSize = "How many coefficients each partition holds (residue_partition_size, section 8.6.1).";
    public const string ResidueClassifications = "How many classifications a partition can have (residue_classifications, section 8.6.1).";
    public const string ResidueClassbook = "The codebook that codes the partitions' classifications (residue_classbook, section 8.6.1).";
    public const string ResidueCascade = "For each classification, which of eight passes code values, as a bit mask (residue_cascade, section 8.6.1).";
    public const string ResidueBooks = "The codebook of each classification's passes (residue_books, section 8.6.1).";
    public const string ResidueBook = "A codebook number (section 8.6.1).";
    public const string Mappings = "The mappings: how a packet's channels are coupled and which floor and residue each channel uses (Vorbis I, section 4.2.4).";
    public const string MappingCount = "How many mappings follow (vorbis_mapping_count, section 4.2.4).";
    public const string Mapping = "A mapping: its submaps, its channel coupling steps and which submap each channel uses (section 4.2.4).";
    public const string MappingType = "The mapping type, which must be 0 in Vorbis I (section 4.2.4).";
    public const string HasSubmaps = "Whether the mapping has more than one submap (section 4.2.4).";
    public const string Submaps = "How many submaps the mapping has, each a floor and residue pair channels can share (vorbis_mapping_submaps, section 4.2.4).";
    public const string HasCoupling = "Whether the mapping couples channels by square polar mapping (section 4.2.4).";
    public const string CouplingStepCount = "How many coupling steps follow (vorbis_mapping_coupling_steps, section 4.2.4).";
    public const string CouplingSteps = "The channel pairs the mapping couples, each coded as a magnitude and an angle (square polar mapping, sections 4.2.4 and 4.3.5).";
    public const string CouplingStep = "A coupling step: one channel carries the magnitude, the other the angle, from which the decoder restores both (section 4.3.5).";
    public const string MagnitudeChannel = "The channel that carries the magnitude (vorbis_mapping_magnitude, section 4.2.4).";
    public const string AngleChannel = "The channel that carries the angle (vorbis_mapping_angle, section 4.2.4).";
    public const string MappingReserved = "A reserved field, which must be 0 (section 4.2.4).";
    public const string ChannelMux = "The submap each channel uses (vorbis_mapping_mux, section 4.2.4).";
    public const string ChannelMuxEntry = "The submap this channel uses (section 4.2.4).";
    public const string Submap = "A submap: the floor and residue its channels are decoded with (section 4.2.4).";
    public const string SubmapTime = "A placeholder for a time configuration, which decoders discard (section 4.2.4).";
    public const string SubmapFloor = "The floor the submap's channels use (vorbis_mapping_submap_floor, section 4.2.4).";
    public const string SubmapResidue = "The residue the submap's channels use (vorbis_mapping_submap_residue, section 4.2.4).";
    public const string Modes = "The modes an audio packet chooses from: each sets the block size and the mapping (Vorbis I, section 4.2.4).";
    public const string ModeCount = "How many modes follow; ilog of one less than it is the number of bits that name a mode in each packet (vorbis_mode_count, section 4.2.4).";
    public const string Mode = "A mode: whether its blocks are long or short, and the mapping they use (section 4.2.4).";
    public const string BlockFlag = "0 for the short block size, 1 for the long one (vorbis_mode_blockflag, section 4.2.4).";
    public const string WindowType = "The window type, which must be 0 in Vorbis I (vorbis_mode_windowtype, section 4.2.4).";
    public const string TransformType = "The transform type, which must be 0, the MDCT, in Vorbis I (vorbis_mode_transformtype, section 4.2.4).";
    public const string ModeMapping = "The mapping the mode's packets use (vorbis_mode_mapping, section 4.2.4).";

    public const string AudioPackets = "The audio packets, from the fourth packet on: each codes one block of the size its mode sets (Vorbis I, section 4.3).";
    public const string Packet = "An audio packet: its type bit, its mode, for a long block the window flags, then the floors and residues of its channels (Vorbis I, section 4.3.1).";
    public const string AudioPacketType = "The packet type bit, 0 for audio; decoders skip a packet with 1 (section 4.3.1).";
    public const string ModeNumber = "The mode the packet uses, which sets its block size (mode_number, section 4.3.1).";
    public const string PreviousWindowFlag = "Whether the previous block is long, which shapes the left half of this long block's window (previous_window_flag, section 4.3.1).";
    public const string NextWindowFlag = "Whether the next block is long, which shapes the right half of this long block's window (next_window_flag, section 4.3.1).";
    public const string AudioData = "The floors and residues of the packet's channels, which the analyzer does not decode (sections 4.3.2 to 4.3.4).";
    public const string UnreadData = "The rest of a packet the analyzer could not read as a block: not audio, or its mode undefined or cut short.";

    public const string Statistics = "Figures over the stream's audio packets, from their blocks and the granule positions.";
    public const string Packets = "How many audio packets the stream holds.";
    public const string LongBlocks = "Packets that code a long block, and their share of the packets that code a block.";
    public const string ShortBlocks = "Packets that code a short block, and their share: encoders switch to short blocks at sudden sounds.";
    public const string DecodedSamples = "The samples a decoder returns before trimming: each block returns a quarter of the previous block and a quarter of its own, the first nothing (section 4.3.8).";
    public const string StartOffset = "The first audio page's granule position less the samples returned up to it: 0 for a stream that starts at its beginning, below 0 where samples are discarded at the start, above where the stream starts later (section A.2).";
    public const string StartTrimming = "Samples a decoder discards at the start because the first granule position is smaller than the samples returned (section A.2).";
    public const string EndTrimming = "Samples the packets return beyond the last granule position, which a decoder drops from the end (section A.2).";
    public const string Samples = "The samples a decoder outputs: the decoded samples less what the start and end trimming drop.";
    public const string Duration = "The samples over the sample rate, in seconds.";
    public const string StatIsVbr = "Whether the audio packets differ in size, so the bitrate changes from packet to packet.";
    public const string AverageBitrate = "The bits of the audio packets over the samples they return.";
    public const string MinimumBitrate = "The lowest bitrate of an audio packet: its bits over the half block of samples it codes.";
    public const string MaximumBitrate = "The highest bitrate of an audio packet: its bits over the half block of samples it codes.";
    public const string PacketSizes = "How many different sizes the audio packets have, and the range of sizes for each block size.";
    public const string PacketSizesPerBlock = "The smallest, largest and average size of the packets that code blocks of this size.";
    public const string ModeUsage = "How many audio packets use each mode of the setup header.";
    public const string ModeShare = "Packets of this mode, and their share of the packets that code a block.";

    public const string Encoder = "Which library or program encoded the stream, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: high for a vendor string a libvorbis release writes, medium for an encoder's name in a field other software may write.";
    public const string Evidence = "What points to the encoder, at the bytes it was found in.";
}

internal static class SeriesText
{
    public const string Bitrate = "The bitrate of each audio packet: its bits over the half block of samples it codes, 0 for a packet that codes no block.";
    public const string BitrateOverTime = "The bitrate in each second of audio: the bits of the packets whose blocks start in it over the samples those blocks code.";
}

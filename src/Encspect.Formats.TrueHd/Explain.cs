namespace Encspect.Formats.TrueHd;

// What each node is, in the words the report shows next to it. Constants only: a stream holds a thousand access units a second, and a
// description picks one of these and never allocates. No public specification of the bitstream was read for the analyzer: the sources are
// FFmpeg's reading of it, as docs/design/truehd-format.md states it, and the files the analyzer was tested on. Where only FFmpeg's encoder
// names a field, the text says so, and where nothing settles a field's meaning, it says that too.
internal static class Explain
{
    public const string TrueHdStream = "The access units of a Dolby TrueHD stream, one after another with nothing between them. Each holds 40, 80 or 160 samples of every channel, as the sampling rate sets, and is decoded without loss.";
    public const string MlpStream = "The access units of an MLP (Meridian Lossless Packing) stream, one after another with nothing between them. Each holds 40, 80 or 160 samples of every channel, as the sampling rate sets, and is decoded without loss.";
    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies between the access units, which the tag breaks; the analyzer does not read them as access units.";
    public const string SkippedData = "Bytes that are no access unit, such as damaged data; the analyzer skipped them to the next access unit whose major sync checks out.";
    public const string AccessUnit = "An access unit: a four-byte header, a major sync where it has one, the substream directory and the data of each substream. It holds 40, 80 or 160 samples of every channel; a unit decodes only after a major sync has stated the stream's parameters.";
    public const string UnitBeforeSync = "An access unit before the first major sync. It cannot be read, because the number of its substreams and the sampling rate are not known yet; a stream cut out of a longer one starts like this.";
    public const string CutUnit = "An access unit that the data ends inside.";
    public const string UnitHeader = "The four bytes every access unit starts with: a check nibble, the unit's length and an input timing.";
    public const string CheckNibble = "Makes the parity of the unit header and the substream directory come out right: XOR their bytes together, then the two nibbles of the result, and the answer is 0xF.";
    public const string UnitLength = "The length of the whole access unit, its header included, from 4 to 8,190 bytes; the stream states it in 16-bit words.";
    public const string InputTiming = "A 16-bit counter in sample periods that wraps around. FFmpeg's decoder does not use it.";
    public const string IsParityValid = "Whether the parity of the unit header and the substream directory comes out right.";

    public const string MajorSync = "The stream's parameters, at the start of some access units: format, sampling rate, channels and the number of substreams. Decoding can start only at an access unit that has one.";
    public const string SyncWord = "The 24 bits 0xF8726F that start a major sync.";
    public const string StreamType = "0xBA for Dolby TrueHD, 0xBB for MLP.";
    public const string FormatInfo = "32 bits that describe the format. For Dolby TrueHD they hold the sampling rate and the channel assignments of the presentations; for MLP, the bit depths, the sampling rates and the channel arrangement. An MP4 dmlp box repeats them.";
    public const string SamplingRate = "The sampling rate, from a 4-bit code: 48, 96 or 192 kHz, or 44.1, 88.2 or 176.4 kHz. A unit holds 40, 80 or 160 samples of each channel at the first, second or third rate of either family.";
    public const string Group1BitDepth = "The bit depth of the first channel group, from a 4-bit code: 16, 20 or 24 bits. A channel group is a set of channels that share bit depth and sampling rate.";
    public const string Group2BitDepth = "The bit depth of the second channel group, coded like the first's; 0xF means there is no second group.";
    public const string Group1SamplingRate = "The sampling rate of the first channel group, from the code that Dolby TrueHD's sampling rate uses.";
    public const string Group2SamplingRate = "The sampling rate of the second channel group; 0xF means it has none of its own.";
    public const string MultichannelType = "A 4-bit field that FFmpeg's encoder writes as 0 under the name multichannel type; its meaning is not known.";
    public const string ChannelArrangement = "The channels of the stream, by number: 21 arrangements from a single center channel to five channels with an LFE channel.";
    public const string SixChannelType = "The 6-channel multichannel type, a bit that FFmpeg's decoder skips and its encoder writes as 0.";
    public const string EightChannelType = "The 8-channel multichannel type, a bit that FFmpeg's decoder skips and its encoder writes as 0.";
    public const string TwoChannelModifier = "A 2-bit modifier of the 2-channel presentation: 0 stereo or not indicated, 1 Dolby Surround, 2 Dolby Headphone, 3 mono or dual mono.";
    public const string SixChannelModifier = "A 2-bit modifier of the 6-channel presentation: 0 not indicated, 1 not Dolby Digital EX, 2 Dolby Digital EX.";
    public const string EightChannelModifier = "A 2-bit modifier of the 8-channel presentation: 0 not indicated, 1 not Dolby Digital EX, 2 Dolby Digital EX.";
    public const string SixChannelAssignment = "The channels of the 6-channel presentation as 5 flags: left and right, center, LFE, left and right surround, left and right height. Each flag that is set adds its channels, in that order.";
    public const string EightChannelAssignment = "The channels of the 8-channel presentation as 13 flags: the five of the 6-channel presentation, then left and right center, left and right rear surround, center surround, top surround, left and right surround direct, left and right wide, center height and a second LFE.";
    public const string Signature = "A 16-bit value that is 0xB752 in every file the analyzer was tested on; FFmpeg's decoder does not check it.";
    public const string MajorSyncFlags = "A 16-bit word. FFmpeg's decoder skips it, and its encoder writes 0x4000 for MLP and 0 for Dolby TrueHD; what the bits mean is not known.";
    public const string ReservedWord = "Reserved bits, 0 in every file the analyzer was tested on.";
    public const string IsVariableRate = "Whether the stream is variable rate; it is in every file the analyzer was tested on.";
    public const string PeakDataRate = "The stream's peak data rate in sixteenths of a bit per sample period; the report shows it in bits per second.";
    public const string SubstreamCount = "How many substreams every access unit holds, 1 to 4; MLP has at most 2.";
    public const string ExtendedSubstreamInfo = "Two bits that FFmpeg's decoder reads and does not use; its source comments tie them to the 16-channel presentation.";
    public const string SubstreamInfo = "One byte of information about the substreams. In a Dolby TrueHD stream of four substreams, bit 7 set is what FFmpeg takes as Dolby Atmos; its decoder also uses bits 2 to 6 in a rule for skipping substreams.";
    public const string ChannelMeaning = "64 bits that FFmpeg's decoder does not read. Its encoder fills them with a sampling rate code, bit depth and channel occupancy for MLP, and with dynamic range, dialogue normalization, mix levels and source formats for Dolby TrueHD; the analyzer shows them as they are written.";
    public const string Extension = "Extra words after the channel meaning, present where its last bit is set in Dolby TrueHD. The first nibble counts the words; their content is not known.";
    public const string MajorSyncCrc = "A CRC-16 over the major sync's bytes, with the generator x^16 + x^5 + x^3 + x^2 + 1 and the register starting at 0, computed without the zero bits a standard CRC appends. The last two bytes store it, most significant first.";
    public const string Computed = "The value computed over the same bytes.";
    public const string IsValid = "Whether the stored value equals the computed one.";

    public const string SubstreamDirectory = "One entry for each substream: three flags, a reserved bit, and where the substream's data ends. The ends are counted in 16-bit words from the end of the directory, and each substream starts where the one before ends.";
    public const string DirectoryEntry = "The entry of one substream: its flags and the end of its data.";
    public const string HasExtraWord = "Whether a 16-bit word follows this entry; only Dolby TrueHD has them.";
    public const string IsNonRestart = "The non-restart bit: 0 in an access unit with a major sync, where every substream starts with a restart header, and 1 in one without.";
    public const string HasCheckData = "Whether the substream ends with a parity byte and a CRC-8.";
    public const string ReservedBit = "A bit that FFmpeg's decoder skips; it is set in some access units of the real files the analyzer was tested on.";
    public const string SubstreamEnd = "Where the substream's data ends, in 16-bit words from the end of the directory; the report shows bytes.";
    public const string ExtraWord = "A 16-bit word that FFmpeg's decoder skips; what it holds is not known.";

    public const string Substream = "The data of one substream: its blocks, an optional end of stream marker and, where the directory says so, a parity byte and a CRC-8. A substream codes some channels and outputs a presentation, built from them and from the channels of the lower substreams.";
    public const string SubstreamOpaque = "The fourth substream of a stream that signals Dolby Atmos. FFmpeg's decoder never decodes it and its source calls it non-audio data, so the analyzer checks its bytes against the directory and reads no further.";
    public const string RestartHeader = "Resets the substream's parameters and carries the lossless check of the audio since the previous restart header: the channels it codes and outputs, the noise generator's seed, where each matrix channel goes and a CRC-8. Every substream starts with one in an access unit with a major sync.";
    public const string RestartSync = "The 13 bits 0x18F5 that start a restart header; with the noise type bit that follows they read 0x31EA or 0x31EB.";
    public const string NoiseType = "0 gives two noise channels from a shift register, which a matrix can use; 1 uses a table of noise. MLP always has 0.";
    public const string OutputTiming = "A 16-bit counter in sample periods that wraps around. FFmpeg's decoder does not use it.";
    public const string FirstCodedChannel = "The index of the first channel this substream codes.";
    public const string LastCodedChannel = "The index of the last channel this substream codes; one below the first means it codes none.";
    public const string LastMatrixChannel = "The index of the last channel of the presentation this substream outputs: its channel count less 1.";
    public const string NoiseShift = "How many places the noise channels' values are shifted left (noise type 0).";
    public const string NoiseSeed = "The starting value of the 23-bit register that generates the noise.";
    public const string LargestOutputShift = "A field FFmpeg's decoder skips. Its encoder writes the largest output shift of the samples up to the next restart header there, and every file the analyzer was tested on agrees.";
    public const string LargestLsbCount = "A field FFmpeg's decoder skips. Its encoder writes the largest count of least significant bits of the samples up to the next restart header there, and every file the analyzer was tested on agrees.";
    public const string LargestBitDepth = "A field FFmpeg's decoder skips. Its encoder writes the largest bit depth of the output samples up to the next restart header there, and every file the analyzer was tested on has the two such fields equal or one apart.";
    public const string HasBlockSizeCheck = "Whether every block of the substream is bracketed by check data until the next restart header. No file the analyzer was tested on sets it, and FFmpeg asks for a sample file when it meets it.";
    public const string LosslessCheck = "One byte that checks the audio: the XOR of the output samples since the previous restart header of this substream, each shifted left by its channel's number, folded to 8 bits. Only decoding can compute it.";
    public const string RestartSkipped16 = "Sixteen bits that FFmpeg's decoder skips; its encoder writes 0, and the real Dolby TrueHD file has 0 or 0x8000.";
    public const string ChannelAssignments = "Where each matrix channel goes in the presentation's channel list, 6 bits for each of the channels from 0 to the last.";
    public const string ChannelAssignment = "The position in the presentation's channel list of one matrix channel.";
    public const string RestartCrc = "A CRC-8 over the restart header's bits from its sync on, with the generator x^8 + x^4 + x^3 + x^2 + 1 and the register starting at 0, computed without the zero bits a standard CRC appends.";

    public const string Block = "A run of samples coded with one set of parameters. An access unit holds one or more blocks of every substream; each starts with two flags and ends with one that says whether it is the last.";
    public const string DecodingParameters = "The parameters a block may change: the block size, the matrices, the output shifts, the quantizer step sizes and, for each coded channel, its filters and how its residuals are coded. A group whose present bit is 0 keeps its value.";
    public const string PresenceFlags = "Eight flags that say which groups of parameters a block may carry; a restart header sets them all.";
    public const string BlockSize = "How many samples the following blocks hold, from 8 to the unit's samples per channel.";
    public const string Matrices = "The primitive matrices that rebuild the presentation's channels from the decoded ones.";
    public const string Matrix = "One primitive matrix: it replaces one channel by the sum of all channels, each weighted by a coefficient with 14 fractional bits.";
    public const string OutputShifts = "How many places each matrix channel is shifted left on output.";
    public const string QuantizerSteps = "For each channel from 0 to the last one coded, the number of low bits its samples always leave 0.";
    public const string ChannelParameters = "The filters and the residual coding of one coded channel.";
    public const string FirFilter = "The channel's FIR filter, which predicts a sample from up to 8 earlier ones of the same channel: its order, shift, coefficients and the bits they take.";
    public const string IirFilter = "The channel's IIR filter, which predicts a sample from up to 4 earlier errors of the same channel; it may carry the starting values of its state.";
    public const string HuffmanOffset = "A value added to every residual of the channel.";
    public const string Codebook = "Which Huffman code the channel's residuals use: 0 for none, 1 to 3 for the codes that differ in how many values the codeword 1 and its neighbors cover.";
    public const string LsbCount = "How many low bits of each residual are stored apart from the Huffman code, the quantizer step size's bits included.";
    public const string BlockData = "The residuals of the block: for each sample period, the bits that matrices with a bypassed least significant bit take, then one residual for each coded channel.";
    public const string EndOfStream = "Marks the end of the stream: 0xD234 and a second word. In Dolby TrueHD the second word can state how many samples at the end of the unit are not output; in MLP it is 0xD234 again.";
    public const string DroppedSamples = "How many samples at the end of this unit, in this substream, are not output.";
    public const string ParityByte = "A byte that makes the XOR of all bytes of the substream before it come out as 0xA9.";
    public const string SubstreamCrc = "A CRC-8 over the substream's bytes before the parity byte, with the generator x^6 + x^5 + x + 1 and the register starting at 0x3C over all of them but the last, XORed with that last byte.";
    public const string Padding = "Zero bits that bring the substream's blocks to a multiple of 16 bits.";

    public const string Statistics = "Figures computed over the whole stream.";
    public const string StatUnits = "How many access units the stream holds, counted from the first major sync.";
    public const string StatMajorSyncs = "How many access units carry a major sync.";
    public const string StatSamples = "The samples of each channel, summed over the access units that could be read, less those an end of stream marker drops.";
    public const string StatDuration = "The samples divided by the sampling rate.";
    public const string StatRate = "The sampling rate the first major sync states.";
    public const string StatChannels = "The channels of the highest presentation the analyzer decodes, which is the one with the most channels among the first three substreams.";
    public const string StatLayout = "The speakers of that presentation, in the order its channel list gives.";
    public const string StatBits = "The bit depth of the stream's samples: group 1's for MLP. Dolby TrueHD states none and is reported as 24 bits, as FFmpeg does.";
    public const string StatSubstreams = "How many substreams each access unit holds.";
    public const string StatPresentations = "The channel counts of the presentations the substreams output, lowest substream first.";
    public const string StatSignalsAtmos = "Whether the stream signals Dolby Atmos: four substreams and bit 7 of the substream info set. This is what the stream says, not a finding about its content.";
    public const string StatRestartHeaders = "How many restart headers the stream holds.";
    public const string StatVerifiedParity = "In how many access units the parity of the unit header and the directory came out right.";
    public const string StatFailedParity = "In how many access units it did not.";
    public const string StatVerifiedSyncs = "How many major syncs have a CRC that holds.";
    public const string StatFailedSyncs = "How many major syncs have a CRC that differs from the one computed.";
    public const string StatVerifiedRestarts = "How many restart headers have a CRC-8 that holds.";
    public const string StatFailedRestarts = "How many restart headers have a CRC-8 that differs from the one computed.";
    public const string StatVerifiedSubstreams = "How many substreams have a parity byte and a CRC-8 that hold; the fourth substream of a Dolby Atmos stream is not counted.";
    public const string StatFailedSubstreams = "How many substreams have a parity byte or a CRC-8 that differs from the one computed.";
    public const string StatUncheckedSubstreams = "How many substreams the directory says have no parity byte and CRC-8.";
    public const string StatLosslessChecks = "How many restart headers close an interval whose audio their lossless check byte covers: all but the first of each substream and the first after an end of stream marker. The analyzer compares them when it decodes the audio.";
    public const string StatLeadingUnits = "How many access units come before the first major sync; they are not decoded.";

    public const string BitrateSeries = "The bitrate of each run of 16 access units at the byte offset it starts at.";
    public const string BitrateOverTimeSeries = "The bitrate over each second of audio, from the access units that start in it.";
    public const string AverageBitrate = "The bytes of all access units over the duration they hold.";
    public const string BitrateMode = "Constant where every run of 16 access units has the same bitrate, else variable; unknown with a single run.";
    public const string Smallest = "The shortest access unit of the stream.";
    public const string Largest = "The longest access unit of the stream.";
    public const string MinimumBitrate = "The lowest bitrate of any run of 16 access units.";
    public const string MaximumBitrate = "The highest bitrate of any run of 16 access units.";
    public const string Distribution = "How the runs of 16 access units spread over bitrates.";
    public const string DistributionBin = "The runs of 16 access units whose bitrate lies in this range.";
}

# WAV and Wave64

Two format parsers in `src/Encspect.Formats.Wav`: "WAV" for WAVE files in a RIFF container and "Wave64" for Sony's Wave64 (`.w64`, see [Wave64](#wave64)). They share the readers of the `fmt `, `fact` and `data` chunks, the routes to the codec parsers and the checks over the stream.

WAVE files in a RIFF container (`.wav`, `.wave`): RIFF for little-endian integers, RIFX for big-endian ones, and RF64 and BW64 for sizes of 64 bits. The `pcm` decoder reads the samples of PCM, floating-point, A-law and mu-law data (see [Decoding](#decoding)); it is used for measurements only, since WAVE stores no checksum.

Sources:

- Microsoft's and IBM's Multimedia Programming Interface and Data Specifications 1.0 (RIFF, the WAVE form, `fmt `, `fact`, `data`, `cue `, `plst`, `LIST` with `INFO`, `adtl` and `wavl`, `smpl`, `inst`) and the Multimedia Data Standards Update of April 1994.
- Microsoft's WAVEFORMATEX and WAVEFORMATEXTENSIBLE documentation, "Multiple channel audio data and WAVE files" and "Audio Subtype GUIDs", with the identifiers of `mmreg.h`, `ksmedia.h`, `ksuuids.h` and `mfapi.h` (the mingw-w64 headers).
- EBU Tech 3285 (`bext`), EBU Tech 3306-2009 (RF64, `ds64`, the cue points), ITU-R BS.2088-2 (BW64, `ds64`, `chna`, `axml`, `bxml`, `sxml`; free from the ITU at <https://www.itu.int/rec/R-REC-BS.2088>) and the iXML specification.
- FFmpeg 6.1.1 and 8.0.1 (`riffdec.c`, `wavdec.c`) and MediaInfo 26.01 are cross-checks of what the fixtures hold, not sources of the format.

## What is read

- The RIFF header: the chunk ID (`RIFF`, `RIFX`, `RF64`, `BW64`), the size and the form type `WAVE`.
- Every chunk is a node. A chunk has a 4-character ID, a 32-bit size, the data and, for an odd size, a pad byte. A chunk the parser does not know is named by its FourCC and shows its size.
- `fmt `:
  - WAVEFORMATEX: the format tag, channels, sample rate, average bytes per second, block alignment, bits per sample and the extra data;
  - WAVEFORMATEXTENSIBLE: the valid bits (the samples per block where the container size is 0), the channel mask and the sub-format GUID, which resolves to the format tag the sub-format stands for or, for AC-3, E-AC-3 and MPEG-2 audio, to the GUID Microsoft's headers give them;
  - a `fmt ` chunk of only the 14 bytes of common fields has no bits per sample (`wav.format-short`); the format and the stream are still read from the fields it has;
  - the extra fields of the compressed formats: Microsoft ADPCM (samples per block and the coefficient sets, the first 64 listed), IMA ADPCM and GSM 6.10 (samples per block), MPEG1WAVEFORMAT and MPEGLAYER3WAVEFORMAT.
- `fact` (sample length), `data` (the first one is the stream; further ones are `wav.data-repeated`) and `ds64` (the RF64 sizes and the table of larger chunks).
- `LIST`:
  - `INFO`: each text chunk by its name from the specification (`INAM` Name, `IART` Artist, `ISFT` Software and so on);
  - `adtl`: `labl`, `note`, `ltxt` and `file`;
  - `wavl`: `data` and `slnt` chunks.
- `bext` (EBU broadcast extension: description, originator, time reference, version, UMID and, from version 2, the loudness values), `cue ` (cue points), `plst` (playlist), `smpl` (sampler: manufacturer, MIDI unity note, loops), `inst` (instrument), `chna` (channel allocation of the Audio Definition Model), `iXML` and `axml` (XML as text), `bxml` (the compression method; the compressed XML is not decompressed) and `sxml` (its header; the sub-chunks and alignment points are not read), `CSET`, `JUNK` and `PAD ` (filler).
- `id3 ` and `ID3 `: the chunk's data is handed to the ID3 tag parser through `ParseTag`, so an ID3v2 tag in a WAVE file is shown as any other.
- `levl`, `link`, `dbmd`, `qlty` and `r64m` are named and sized; their contents are not read.
- Limits, so that a small file cannot ask for a large report:
  - at most 2,048 chunks are listed at one level (`wav.chunk-limit`), and tables inside chunks (cue points, loops, `ds64` and `chna` entries) list at most 1,024 entries;
  - LIST chunks nest at most 8 levels (`wav.list-nesting`);
  - the tree holds at most 250,000 nodes in all, as MP4's and Matroska's do: past that the rest of the level is one "Chunks Not Shown" node and is not read (`wav.node-limit`, warning), and the findings about a missing `fmt ` or `data` chunk are left out;
  - the `ds64` table reads its first 1,024 entries and keeps those whose ID is four printable characters, since only a top-level chunk can use an entry; the rest are skipped;
  - the cue point names, kept to find a name twice and a label for a cue point the file does not hold, are limited to 65,536; with more cue points neither check is made.
- The samples are not read. The figures come from the headers.

## Sizes, placeholders, RF64 and BW64

- A RIFF size or a `data` size of `0xFFFFFFFF` is a placeholder of a writer that could not seek back (FFmpeg writing to a pipe does this). The data then runs to the end of the file (`wav.riff-size-placeholder`, `wav.data-size-placeholder`). A `data` size of 0 followed by no chunk is treated the same way. In an RF64 or BW64 file a `0xFFFFFFFF` RIFF size stands for the size in the ds64 chunk, and the finding says why that size could not be used: no ds64 chunk with the three sizes, or one that does not come first (its size is then compared with what the file holds). A `data` chunk inside a LIST other than `wavl` is not the audio.
- In an RF64 file the 32-bit sizes of `0xFFFFFFFF` are resolved from `ds64`: the RIFF size, the `data` size, the sample count and a table of the other chunks that need 64 bits. A missing `ds64` is `wav.ds64-missing`, one that is not first `wav.ds64-not-first`, and sizes that disagree with the file `wav.ds64-riff-size-mismatch` and `wav.ds64-data-size-mismatch`.
- **BW64** (ITU-R BS.2088-2) works as RF64 does, with these differences:
  - the size of the BW64 chunk "shall be" `0xFFFFFFFF` (section 3.2); another value is `wav.bw64-size-not-placeholder`, and is used as it stands, as the specification's rule for the 32-bit fields says (section 2.4);
  - the `ds64` chunk is mandatory and first (section 2.4); its third 64-bit value is a dummy that a reader ignores and a writer sets to zero (section 4.2), shown as "Dummy" and reported as `wav.ds64-dummy-not-zero` (info) when it is not zero; the sample count of RF64 is not used, since there is no `fact` chunk to resolve;
  - the missing `ds64` chunk and the one that is not first are the same findings as for RF64 (`wav.ds64-missing`, `wav.ds64-not-first`), worded for BW64;
  - the `fmt ` chunk has a `cbSize` that "if present shall be set to zero" and no extra data (section 2.6, "not to be used in BW64"), while section 2.6.2 asks readers to handle the extensible format tag 0xFFFE "in a sensible manner": `wav.bw64-format-extra-data` is raised for extra data outside the extensible form, and exempting the extensible form's own extra fields is the analyzer's reading of that.
- The three sizes are the least a `ds64` chunk gives (24 bytes); a chunk without the table length after them (under 28 bytes) is `wav.ds64-too-short` and lists no chunk, and one that holds fewer than the three sizes is `wav.chunk-too-short` and gives none, so the sizes it should have resolved are `wav.chunk-size-unresolved`.
- `DeclaredEnd` is the RIFF chunk's own end (for RF64 from `ds64`). It is what lets the host tell a tag after the chunk from a tag inside it; a size that is 0, `0xFFFFFFFF` or under 4 states no end.
- Bytes inside the RIFF chunk after its last chunk, too few for another, are `wav.trailing-bytes`; any bytes after the end the RIFF chunk states are `wav.data-after-riff` (whether they look like a chunk is not checked). A chunk cut by the end of the file is `wav.chunk-past-end` and the file `wav.truncated`.
- A pad byte that is missing where the next chunk starts right after an odd chunk is `wav.pad-byte-missing`; the parser follows the layout that gives a valid chunk ID next. A nonzero pad byte is `wav.pad-byte-not-zero`.

## Reported

A WAVE file of linear PCM, IEEE float, A-law or mu-law reports a stream: codec, sample rate, channels, layout, bits per sample, the samples from the `data` size and the block alignment, the duration and the average bitrate. The statistics node lists them, with the container width where it differs from the valid bits, the sample format and the byte order.

- A compressed format (Microsoft ADPCM, IMA ADPCM, GSM 6.10, ATRAC, WMA, Vorbis in WAVE and so on) is named by its format tag. The stream reports the codec name, the rate and channels from the header, the duration from `fact` where there is one, and the bitrate from the header's average bytes per second. The coding is not analyzed (`wav.coding-not-analyzed`, info).
- A tag with no name in the table is `Other` and `wav.coding-unknown`.
- Routes: the data chunk goes to the codec parser through `ParseStream`, with no configuration (the codec parsers take nothing from the `fmt ` chunk, and AC-3's would read its extra bytes as a dac3 or dec3 box):
  - MPEG audio (tags 0x0050 and 0x0055), AC-3 (0x2000) and DTS (0x0008 and 0x2001), with codec IDs `mpeg-audio`, `ac-3` and `dts`;
  - the sub-format GUIDs of Microsoft's headers that have no format tag: MEDIASUBTYPE_DOLBY_AC3 (`e06d802c-db46-11cf-b4d1-00805f6cbbea`, `ac-3`), MFAudioFormat_Dolby_DDPlus (`a7fb87af-2d02-42fb-a4d4-05cd93843bdd`, E-AC-3, `e-ac-3`; FFmpeg's muxer writes E-AC-3 with it) and MEDIASUBTYPE_MPEG2_AUDIO (`e06d802b-db46-11cf-b4d1-00805f6cbbea`, `mpeg-audio`).
- The routed parser's stream is the file's stream; `wav.format-differs-from-stream` is raised where the header's channels (plain WAVEFORMATEX only) or sample rate disagree with what the codec parser read.
- **DTS in PCM samples.** A DTS stream in the 16-bit stereo samples of a little-endian PCM file, as on a DTS music CD, is handed to the DTS parser: the codec parser (`ICodecParser.Recognizes`) must find a chain of consistent core frames at the start of the data chunk, after any silence, in any of the four forms. The stream is then the DTS stream (its rate and channels, not the carrier's) and `wav.dts-in-pcm` (info) says the `fmt ` chunk describes the carrier. A sync word alone is no stream, and other widths, channel counts and byte orders are samples of their own format. The file stays a WAV file (class 100); the DTS parser's raw-file chain inside the data is not listed as an alternative, since it is made of the WAV file's bytes. The DTS parser's findings about silence before the first frame (`dts.leading-junk`) stand.

## IEC 61937 bursts in the samples

S/PDIF passthrough captured or rendered to a WAVE file (or a Wave64 file) writes the bursts of IEC 61937 as 16-bit stereo samples. The first data chunk is checked for them when the format is 16-bit stereo PCM or the format tag 0x0008 (DTS; Microsoft's sub-format of DTS over IEC 61937 is that tag's) in a little-endian file, or the format tag is 0x0092, 0x0240 or 0x0241 (Microsoft's names for AC-3 over S/PDIF, and synonyms of it for IEC 61937 data, whatever the bits and channels say).

- **What counts as a stream:** a chain, after zero frames and any pause or null bursts (at most 1 MiB of them together, so that a capture which starts in a gap of the stream is found), of three bursts in a row at a frame of 4 bytes: each has the sync words F872h and 4E1Fh (as little-endian samples), the same audio data type and bitstream number, a payload that is not empty and fits the repetition period of the data type after the 8 bytes of the preamble, and starts one repetition period after the one before. A sync word alone, two bursts, other sample widths and channel counts, a data type without a fixed period, and silence over 1 MiB are PCM. The rule is the sync rule of CLAUDE.md (a chain, not a signature).
- **Data types (IEC 61937-2:2021, Table 2):** AC-3 (1), MPEG-1 Layer I (4), MPEG-1 Layer II or III or MPEG-2 without extension (5), MPEG-2 with extension (6), MPEG-2 AAC (7), the MPEG-2 low sampling frequency layers (8 to 10), DTS types I, II and III (11 to 13), ATRAC (14), ATRAC 2/3 (15), ATRAC-X and its low latency forms (16), WMA Professional types I to IV (18), MPEG-2 and MPEG-4 AAC low sampling frequency and MPEG-4 AAC (19, 20), E-AC-3 (21) and MAT (22), each with the repetition period of the table (in IEC 60958 frames, four bytes each) and the unit of the length code (bits, except bytes for E-AC-3 and MAT). Null data (0) and the pause burst (3) carry no audio: the walk steps over each by its length code (the preamble and the payload of Pd bits, up to a whole frame) and counts them and the runs of them with no other burst between, without listing them, so that a gap of pause bursts at their recommended period of 3 frames (IEC 61937-3:2017 Table 2 for AC-3 and E-AC-3) costs a few bytes of reading each and none of the listed slots. The other data types of the table, whose period depends on the part of the standard, end the search.
- **Routes:** the payload of each burst of the stream's data type, with the bytes of each 16-bit word swapped back to the coding's order, is one packet for `ParsePackets`: AC-3 (`ac-3`), E-AC-3 (`e-ac-3`) and DTS types I to III (`dts`). The nodes of the codec parser point at the burst's bytes in the file (to the word, since a pair is swapped). The stream is the coding's: its rate, channels and duration are the codec parser's, and `wav.iec61937-in-pcm` (info) says the `fmt ` chunk describes the carrier. A file reads as a WAV (or Wave64) file; it is not offered as an alternative of the raw AC-3 or DTS parsers.
- **AAC:** the payload of an AAC burst (data types 7, 19, 20, 51, 52, 84 and 116) is an ADTS frame: every payload of the fixture starts with an ADTS header once the pairs are swapped back, and Microsoft names its sub-format "MPEG-2/4 AAC in ADTS". The frames go to the AAC project's `aac-adts` codec parser as packets (see [aac.md](aac.md)): the first frame's header gives the configuration and each frame's raw data block is read as an access unit. The stream is the coding's (its rate, channels and samples are the AAC parser's, `wav.iec61937-in-pcm` says the `fmt ` chunk describes the carrier), but the link rate is not compared with the sampling rate: nothing read says how they relate for the low sampling frequency types and for a stream with SBR.
- **Codings without a parser here:** MPEG audio (data types 4 to 6 and 8 to 10), ATRAC, WMA Professional and MAT are not routed; MPEG audio is a later item with "MPEG audio as packets" (the MPEG audio parser reads only one contiguous payload), the others have no parser. Their bursts are walked, and the stream is named by the data type and timed by its bursts (`wav.iec61937-payload-not-analyzed`, info): the bursts times the repetition period, counted in IEC 60958 frames, over the sample rate of the `fmt ` chunk, which is the rate of those frames; the stream states no sample rate, channels or samples, since the link runs at twice the sampling rate of the coding for the low sampling frequency types and nothing read says the relation for the others, and the bitrate is that of the payloads over that time.
- **Nodes:** an "IEC 61937 Bursts" node under the data chunk, from the first burst of any kind, with the first 1,024 bursts of the stream's audio data type and any burst of another audio type among them (preamble words, burst-info with its data type, error flag, information and bitstream number, length code, payload); further bursts are counted, and pause and null bursts are counted in `wav.iec61937-pause-bursts` and never listed. The nodes count against the budget of the tree.
- **Findings:**
  - `wav.iec61937-burst-missing`: no burst where the repetition period puts one, the next sync words are later; `wav.iec61937-burst-spacing`: the next burst starts earlier than the period;
  - `wav.iec61937-length-invalid`: the length code is zero, or states more than fits the period or the data (the payload is read as far as it fits, and a payload of an odd number of bytes that the data ends in loses its last byte, which is the second byte of a sample the file does not hold);
  - `wav.iec61937-type-changed`: a burst of another audio data type or bitstream number among those of the stream (skipped);
  - `wav.iec61937-error-flag` (info): Pc bit 7 is set;
  - `wav.iec61937-pause-bursts` (info): the number of pause and null bursts and of the runs they form;
  - `wav.iec61937-data-after-bursts`: bytes after the last burst that are not zero;
  - `wav.iec61937-rate-mismatch`: the sample rate of the `fmt ` chunk is not the sampling rate of the stream, or four times it for E-AC-3.
- **Sources:** the freely available previews of IEC 61937-1:2021 (<https://cdn.standards.iteh.ai/samples/101993/f9c9621694de4bdcbc934f16d281bf1d/IEC-61937-1-2021.pdf>: clauses 1 to 6.1.8 and Tables 1 to 8, the burst-preamble and burst-info), IEC 61937-2:2021 (<https://cdn.standards.iteh.ai/samples/103894/b95748149ab74305bb621367b9a2df03/IEC-61937-2-2021.pdf>: Tables 1 to 3, the data types with repetition periods and units) and IEC 61937-3:2017 (<https://cdn.standards.iteh.ai/samples/22796/aa2d391f8cb8477395c957abe293911a/IEC-61937-3-2017.pdf>: AC-3 and E-AC-3 burst-info and the AC-3 burst), and Microsoft's "Representing Formats for IEC 61937 Transmissions" (<https://learn.microsoft.com/en-us/windows/win32/coreaudio/representing-formats-for-iec-61937-transmissions>: the link rate of E-AC-3, four times the content rate). The standard itself is not free. Fixtures were written with FFmpeg 6.1.1's `spdif` muxer used as a black box; its source was not read.
- **Not verified:**
  - the later clauses of Part 1 (6.2, 6.3 stuffing, 7 the pause and null bursts) and Parts 4 to 15 (the payloads of MPEG audio, DTS, AAC and the rest, including how DTS 14-bit streams and DTS-HD are carried) are not in the previews: stuffing is not checked except that what follows the last burst is zeros, pause and null bursts are recognized by their data type alone and stepped over by their length code in bits, and the order of the payload's bytes for DTS and the others was taken from what FFmpeg wrote and from the AC-3 syncword (the pairs are swapped in the files of the muxer);
  - the extended preamble of data type 31 (Pe and Pf) is not read; DTS type IV, DTS-HD, MAT and Dolby TrueHD over HDMI (`ksmedia.h` subtypes) are not routed;
  - a format tag of 0x0092 is taken to hold bursts because of Microsoft's name for it; no tool wrote such a file, and the fixture is hand-built;
  - bursts of a bitstream number other than the stream's are skipped, not read;
  - the previews do not define the reference points of the repetition period (R-AC-3, R-MAT): the walk measures the spacing from one preamble's Pa to the next;
  - that the link runs at twice the sampling rate for the low sampling frequency types is inferred from the doubled periods of Table 2 and from one fixture (`external-iec61937-mp2-lsf-24000`), and the rate relation of the AAC types is not compared for that reason;
  - that the payload of the MPEG-4 AAC data types (20, 52, 84 and 116) is an ADTS frame rests on Microsoft's one sub-format, "MPEG-2/4 AAC in ADTS", and on the one fixture, which has data type 7; a payload that is no ADTS frame gives `aac.adts-packet-invalid` and no configuration;
  - the sub-format GUIDs Microsoft lists for IEC 61937 (`KSDATAFORMAT_SUBTYPE_IEC61937_*`, those of E-AC-3, MPEG, AAC, DTS-HD and MLP have `-0cea-` in them) are not looked for; the DTS one is the format tag 0x0008 of the base GUID, which is.

## Findings on the format chunk

- Findings about what a file holds are warnings, as in the other format parsers (an error is the analysis failing, the host's `parse.*`); the few information findings say what the analyzer did not do or what it chose.
- A second `fmt ` is `wav.format-repeated`; a missing one `wav.no-format-chunk`; a `data` chunk before it `wav.data-before-format`.
- Zero channels, a zero sample rate and zero bits per sample for a linear format are `wav.channels-zero`, `wav.sample-rate-zero` and `wav.bits-per-sample-zero`.
- `wav.block-align-mismatch` and `wav.average-bytes-mismatch` compare the header's block alignment and average bytes per second with the values the channels, the rate and the width give.
- `wav.float-bits` (floating point other than 32 or 64 bits), `wav.companded-bits` (A-law or mu-law other than 8 bits).
- `wav.format-extra-size`, `wav.extensible-incomplete` (a cbSize under 22, or a fmt chunk of 16 or 17 bytes with no cbSize), `wav.extensible-container-bits` (a container size that is not a whole number of bytes, which WAVEFORMATEXTENSIBLE requires), `wav.valid-bits-exceed-container` and `wav.channel-mask-mismatch` (info: a mask that names a different number of speakers than the channels, which Microsoft's rules cover; see below).
- `wav.fact-missing` (info: a format that is not PCM or floating point, or PCM in a `wavl` list, needs a `fact` chunk), `wav.fact-samples-mismatch` (the `fact` samples against the audio, silence of a `wavl` list included), `wav.partial-sample-frame` (a data size that is not a whole number of sample frames), `wav.no-data-chunk`, `wav.no-audio`.
- `bext`: `wav.bext-reserved-not-zero` (info) checks the bytes reserved in the version: 254 in version 0 (the UMID and loudness area included), 190 in version 1 and 180 in version 2 (EBU Tech 3285, section 1.1).

## BW64 chunks: chna and the XML chunks

- `chna`: a count of tracks and of UIDs, then structures of 40 bytes (track index, `ATU_` UID, track format reference, pack format reference, pad). The checks are those of section 8.2, and they run when the size and the count fit (`wav.chna-size` otherwise):
  - `wav.chna-uid-count-mismatch`: the number of UIDs "shall match" the structures in use, those with a track index from 1;
  - `wav.chna-track-count-mismatch` (info): the number of tracks against the different track indexes in use ("even if a track contains more than one set of IDs, it is still just one track"); section 8.2 has no "shall" for this field, only "the number of tracks used in the file", so counting the different indexes is the analyzer's reading and the finding says so;
  - `wav.chna-unused-entry-not-zero`: a structure with track index 0 holds IDs, where the specification wants null strings;
  - `wav.chna-id-format`: an ID of a structure in use is not `ATU_` and 8 hexadecimal digits, `AT_` and 8 digits, `_` and 2 digits (or `AC_`, 8 digits, `_00`), and `AP_` and 8 digits or all null; the digits are not checked against the common definitions of BS.2094 or the XML;
  - `wav.chna-track-index-out-of-range`: a track index beyond the channels of the `fmt ` chunk.
- The XML chunks (section 9): `wav.xml-chunk-repeated` for a second `axml`, `bxml` or `sxml` chunk, and `wav.adm-without-chna` when an `axml` chunk, or an uncompressed `bxml` chunk, holds the Audio Definition Model's root element `audioFormatExtended` (found by a scan of its first MiB) and the file has no `chna` chunk. XML is not parsed or checked, a `bxml` chunk of gzip is not decompressed, and ADM in both `axml` and `bxml` is not detected as such, since only the first chunk that holds the root element is kept (also where both are uncompressed) and a compressed chunk is not scanned.
- `ubxt` and the XML structure for converting `bext` (section 11) are not read; a file named `.bw64` (section 12 asks readers to be tolerant of it) is detected by its bytes like any other, and `--as bw64` is an alias of `--as wav`.

## Channel layout

- The channel mask of WAVEFORMATEXTENSIBLE gives the speakers in the order of its bits (front left, front right, front center, LFE, back left, back right and so on up to the top speakers). The channels follow that order.
- Bits 18 and above are no WAVE speaker positions: Microsoft reserves them, and EBU Tech 3306-2009 section 3 assigns bits 23 to 30 (bitstreams, control samples and the stereo down mix). The analyzer counts each as a channel without a position (discrete); bit 31, SPEAKER_ALL, is left out of the count that `wav.channel-mask-mismatch` compares with the channels.
- A mask and a channel count that disagree follow Microsoft's rule ("Multiple channel audio data and WAVE files", "Details about dwChannelMask"): with fewer channels than bits the extra, most significant bits are ignored, and with more the remaining channels are assigned to no speaker (discrete). The layout is built that way, and `wav.channel-mask-mismatch` is an information finding. A mask of 0 gives no layout.
- The sub-format GUID is read with its first three groups little-endian, as Windows stores it, also in a RIFX file; the format tag is its first four bytes read little-endian. Microsoft's documents say nothing of RIFX, so this byte order is the analyzer's choice, not a rule from a specification.
- Without a mask, one channel is front center and two are front left and front right (`ksmedia.h`, `KSAUDIO_SPEAKER_MONO` and `KSAUDIO_SPEAKER_STEREO`). More channels have no layout.
- A layout is named (5.1, 7.1 and so on) where it matches a common arrangement.

## Encoder and writer detection

- The `ISFT` chunk of `LIST INFO` (Software) is a claim of the writer, with the weight a tag text has. It is shown as "Writer" with its source. An ID3v2 or APEv2 tag that names another writer gives `wav.writer-claim-differs`.
- The `bext` originator and the `iXML` text are shown but not used as evidence.
- No audio encoder is detected: the samples are read only to be decoded for measurements, and a compressed stream's encoder is the codec parser's business.

## Decoding

The samples of a WAVE or Wave64 file are the data, so the decoder `pcm` (`Encspect.Formats.Wav.Decoding`, also used by the AIFF parser) only unpacks them. It is described in [decoders.md](../design/decoders.md) and [spectral-analysis.md](../design/spectral-analysis.md) ("WAV, Wave64 and AIFF: a PCM decoder").

- **Which streams get a session.** The parser starts one for the first `data` chunk of a stream it reports as:
  - integer PCM of 1 to 8 bits (unsigned bytes), and of 9 to 32 bits (signed, in 2 to 4 bytes), little-endian, or big-endian in RIFX; a container wider than 32 bits (40 or 64 bits) starts a session that the decoder refuses (`decode.not-possible`, info);
  - IEEE float of 32 and 64 bits, and A-law and mu-law of 8 bits, expanded to 16-bit integers (ITU-T G.711);
  - the same under the extensible format tag, by the sub-format.
- **Which do not.** No session, and the stream is reported as before, for:
  - a coding that stores no samples as they are (ADPCM, GSM and the rest) and data that goes to a codec parser (MPEG audio, AC-3, E-AC-3, DTS), including DTS in 16-bit stereo samples and IEC 61937 bursts, recognized or not;
  - a width the coding does not have (`wav.float-bits`, `wav.companded-bits`, no bits per sample);
  - a block alignment that is not the channels times the bytes of a sample (`wav.block-align-mismatch` where it applies), so the layout of the frames is not known;
  - samples in the pieces of a `wavl` list, whose silence the stream counts.
- **What is output.** The container's value as stored, whatever the valid bits (`wValidBitsPerSample`) say: 20 valid bits in a 24-bit container are output as the 24-bit values, and a file that pads at the low end shows the unused bits in the bit-depth usage. Microsoft's specification ("Data Packing for PCM WAVE Files", read for this) puts the amplitude bits in the most significant bits of the sample's integer and zeros below; the decoder does not rely on it. The A-law and mu-law expansion matches FFmpeg's decoders (checked by the MD5 cross-check). They are not marked as lossy streams.
- **How it is read.** In slices of whole sample frames of 64 KiB (a sample frame of more is read whole: a header may state 65,535 channels), so memory does not grow with the file. Bytes after the last whole sample frame are not decoded (`wav.partial-sample-frame`), and a data chunk cut short by the end of the file is decoded as far as it goes.
- **Verification.** There is no checksum to compare. The stream has a `verification` in the model, for the front ends, that says whether the audio was decoded; the text report, the JSON report and the GUI's "Verified" line leave out a verification that declares no checksum, so a WAVE file's report does not change. `--verify` alone does not read the samples; `--measure` does.
- **Loudness.** The loudness values a `bext` chunk of version 2 states are the file's own, written by its creator and not checked; `--measure` computes the integrated loudness, loudness range, maximum true peak level and maximum momentary and short-term loudness from the samples under the same names, which are other figures (see [spectral-analysis.md](../design/spectral-analysis.md) and `encspect --learn loudness`). The channel weights follow the speakers the file's channel mask gives; a file of three or more channels without a mask has no stated positions and its loudness is `not measured`.
- **Cross-check.** `scripts/fixtures/crosscheck_wav_fixtures.py` compares the MD5 of the decoded samples with `ffmpeg -i f -c:a pcm_<format> -f md5 -` (see [development.md](../development.md)); the differences it explains are in its table, each flagged if the pinned FFmpeg (8.0.1) stops differing: RIFX (FFmpeg 6.1.1 reads the samples as little-endian; 8.0.1 not yet observed), the data FFmpeg takes for IEC 61937 bursts, 24 valid bits in a 32-bit container, and the files FFmpeg refuses.

## What is not covered

- The sub-chunks of `sxml`, the XML of the Audio Definition Model (its IDs against `chna`) and `ubxt`.
- IEC 61937 bursts of MPEG audio, ATRAC, WMA Professional, MAT and the other codings that have no codec parser here (the stream is named and sized, not analyzed), bursts under the `-0cea-` sub-format GUIDs of Microsoft's list, and bursts in samples that are not 16-bit stereo (24-bit carriers, HBR formats over several channels).
- The `CHAN`-style channel descriptions of other containers, and the contents of `levl`, `link`, `dbmd`, `qlty` and `r64m`.
- A check of the samples: WAVE stores no checksum, so a file is verified by nothing but its headers.
- sox was not used for the cross-check: FFmpeg writes the fixtures and ffprobe and MediaInfo read them (sox is not installed on the development machines).

## Wave64

Wave64 (`.w64`) is the layout of a RIFF file with GUIDs for chunk IDs, 64-bit sizes and chunks aligned to 8 bytes. It is its own parser, "Wave64" (`--as wave64`, alias `w64`), and not a variant of the WAV parser: the chunk header, the sizes, the alignment and the end of the file differ, while the readers of the chunks it has in common are the WAV parser's.

What it rests on:

- **Sony's specification was not reachable.** The document the MultimediaWiki points to (VCS's "Informations about Sony Wave64", `vcs.de`) could not be fetched from this environment, the wiki page itself could not be fetched either, and nothing taken from a search engine's summary of them is used. The layout rests on sources that can be read:
  - dr_wav (<https://github.com/mackron/dr_libs>, `dr_wav.h` v0.14.6, public domain or MIT-0), the code of which was read; nothing in Encspect follows it, so it carries no notice. It gives the GUIDs as bytes with their text in comments: riff `66666972-912E-11CF-A5D6-28DB04C10000` and wave `65766177-ACF3-11D3-8CD1-00C04F8EDB8A`, and fmt, fact and data (also junk and smpl) as the four characters followed by `ACF3-11D3-8CD1-00C04F8EDB8A`; that "the reported chunk size includes the size of the header itself", the 24 bytes of the GUID and the size field, which it subtracts; and "W64 = 8 byte alignment";
  - the National Archives' PRONOM entry fmt/951 (<https://pronom.nationalarchives.gov.uk/fmt/951>, "Sonic Foundry WAVE 64"), whose byte sequence at the start of the file is `726966662E91CF11A5D628DB04C10000{8}77617665F3ACD311`: the riff GUID, a size of 8 bytes and the first 8 bytes of the wave GUID;
  - files written by FFmpeg 6.1.1's w64 muxer, read as black-box examples, with ffprobe 8.0.1 and MediaInfo 26.01 reading them the same way: a riff size equal to the file's, chunk sizes that count their 24-byte header, and padding to multiples of 8 bytes. FFmpeg's w64 source and Rockbox's were not read.
- **Unverified:** the LIST GUID `7473696C-912F-11CF-A5D6-28DB04C10000` (in neither dr_wav nor PRONOM; the code names a chunk with it LIST and does not read it), the padding of a chunk whose size is not a multiple of 8, which was observed in FFmpeg's output for data chunks only (dr_wav's own padding rule differs), and the contents of the LIST chunk and the GUIDs of other chunks (markers, the summary list, `bext`, `levl`), which no source that can be checked gives. A chunk whose GUID has the four-character form is named by them and sized.

What is read:

- The riff chunk: its GUID, its size (the whole file, 40 bytes at least) and the form GUID. The GUID of a chunk is shown with the characters it stands for. `DeclaredEnd` is the riff chunk's size; a size under 40 holds no file and is reported as `wav.riff-size-placeholder` together with the values that mean "unknown" (0 and all ones), a code the WAV parser raises for a RIFF size that is none too, a size past the file `wav.truncated`, bytes after the end `wav.data-after-riff` (a tag the host finds there is the host's).
- Every chunk is a node: its GUID, its size (counting the 24 bytes of its header), the data and the padding. `fmt `, `fact` and `data` are read as in a WAVE file, with the same findings (the fmt, fact and data checks, `wav.partial-sample-frame` and the rest), the same routes (MPEG audio, AC-3, E-AC-3 and DTS by format tag or sub-format, and DTS in PCM samples) and the same stream; a `junk` chunk is filler; a `LIST` chunk, a chunk with a four-character GUID and any other GUID are named and sized.
- The findings of the structure: `wav.w64-chunk-size-invalid` (a size under 24, so the rest of the file is not read), `wav.chunk-past-end`, `wav.pad-byte-missing` (the padding after a chunk is cut short by the end of the file), `wav.pad-byte-not-zero`, `wav.trailing-bytes`, `wav.chunk-limit` and `wav.node-limit`, with the same budgets as WAV (2,048 chunks at one level, 250,000 nodes).
- Not covered: the contents of `LIST`, markers and other chunks of Sony's editor, and the `ds64` chunk of RF64 and BW64, which a Wave64 file has no use for.

## Tests and fixtures

See "WAV and AIFF" in [development.md](../development.md).

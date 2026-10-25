# WAV

WAVE files in a RIFF container (`.wav`, `.wave`): RIFF for little-endian integers, RIFX for big-endian ones, and RF64 and BW64 for sizes of 64 bits. There is no decoder; the audio is not decoded.

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
  - the `ds64` table keeps the first 1,024 entries whose ID is four printable characters, since only a top-level chunk can use an entry;
  - the cue point names, kept to find a name twice and a label for a cue point the file does not hold, are limited to 65,536; with more cue points neither check is made.
- The samples are not read. The figures come from the headers.

## Sizes, placeholders, RF64 and BW64

- A RIFF size or a `data` size of `0xFFFFFFFF` is a placeholder of a writer that could not seek back (FFmpeg writing to a pipe does this). The data then runs to the end of the file (`wav.riff-size-placeholder`, `wav.data-size-placeholder`). A `data` size of 0 followed by no chunk is treated the same way.
- In an RF64 file the 32-bit sizes of `0xFFFFFFFF` are resolved from `ds64`: the RIFF size, the `data` size, the sample count and a table of the other chunks that need 64 bits. A missing `ds64` is `wav.ds64-missing`, one that is not first `wav.ds64-not-first`, and sizes that disagree with the file `wav.ds64-riff-size-mismatch` and `wav.ds64-data-size-mismatch`.
- **BW64** (ITU-R BS.2088-2) works as RF64 does, with these differences:
  - the size of the BW64 chunk "shall be" `0xFFFFFFFF` (section 3.2); another value is `wav.bw64-size-not-placeholder`, and is used as it stands, as the specification's rule for the 32-bit fields says (section 2.4);
  - the `ds64` chunk is mandatory and first (section 2.4); its third 64-bit value is a dummy that a reader ignores and a writer sets to zero (section 4.2), shown as "Dummy" and reported as `wav.ds64-dummy-not-zero` (info) when it is not zero; the sample count of RF64 is not used, since there is no `fact` chunk to resolve;
  - the missing `ds64` chunk and the one that is not first are the same findings as for RF64 (`wav.ds64-missing`, `wav.ds64-not-first`), worded for BW64;
  - the `fmt ` chunk "shall" have a `cbSize` of zero or none outside the extensible form (section 2.6.2): `wav.bw64-format-extra-data`.
- The three sizes are the least a `ds64` chunk gives (24 bytes); a chunk without the table length after them (under 28 bytes) is `wav.ds64-too-short` and lists no chunk, and one that holds fewer than the three sizes is `wav.chunk-too-short` and gives none, so the sizes it should have resolved are `wav.chunk-size-unresolved`.
- `DeclaredEnd` is the RIFF chunk's own end (for RF64 from `ds64`). It is what lets the host tell a tag after the chunk from a tag inside it; a size that is 0, `0xFFFFFFFF` or under 4 states no end.
- Bytes after the RIFF chunk are `wav.trailing-bytes`; a chunk after it that looks like part of the file is `wav.data-after-riff`. A chunk cut by the end of the file is `wav.chunk-past-end` and the file `wav.truncated`.
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

## Findings on the format chunk

- Findings about what a file holds are warnings, as in the other format parsers (an error is the analysis failing, the host's `parse.*`); the few information findings say what the analyzer did not do or what it chose.
- A second `fmt ` is `wav.format-repeated`; a missing one `wav.no-format-chunk`; a `data` chunk before it `wav.data-before-format`.
- Zero channels, a zero sample rate and zero bits per sample for a linear format are `wav.channels-zero`, `wav.sample-rate-zero` and `wav.bits-per-sample-zero`.
- `wav.block-align-mismatch` and `wav.average-bytes-mismatch` compare the header's block alignment and average bytes per second with the values the channels, the rate and the width give.
- `wav.float-bits` (floating point other than 32 or 64 bits), `wav.companded-bits` (A-law or mu-law other than 8 bits).
- `wav.format-extra-size`, `wav.extensible-incomplete`, `wav.extensible-container-bits` (a container size that is not a whole number of bytes, which WAVEFORMATEXTENSIBLE requires), `wav.valid-bits-exceed-container` and `wav.channel-mask-mismatch` (info: a mask that names a different number of speakers than the channels, which Microsoft's rules cover; see below).
- `wav.fact-missing` (info: a format that is not PCM or floating point, or PCM in a `wavl` list, needs a `fact` chunk), `wav.fact-samples-mismatch` (the `fact` samples against the audio, silence of a `wavl` list included), `wav.partial-sample-frame` (a data size that is not a whole number of sample frames), `wav.no-data-chunk`, `wav.no-audio`.
- `bext`: `wav.bext-reserved-not-zero` (info) checks the bytes reserved in the version: 254 in version 0 (the UMID and loudness area included), 190 in version 1 and 180 in version 2 (EBU Tech 3285, section 1.1).

## BW64 chunks: chna and the XML chunks

- `chna`: a count of tracks and of UIDs, then structures of 40 bytes (track index, `ATU_` UID, track format reference, pack format reference, pad). The checks are those of section 8.2, and they run when the size and the count fit (`wav.chna-size` otherwise):
  - `wav.chna-uid-count-mismatch`: the number of UIDs "shall match" the structures in use, those with a track index from 1;
  - `wav.chna-track-count-mismatch`: the number of tracks against the different track indexes in use ("even if a track contains more than one set of IDs, it is still just one track"; this is the analyzer's reading of "the number of tracks used in the file");
  - `wav.chna-unused-entry-not-zero`: a structure with track index 0 holds IDs, where the specification wants null strings;
  - `wav.chna-id-format`: an ID of a structure in use is not `ATU_` and 8 hexadecimal digits, `AT_` and 8 digits, `_` and 2 digits (or `AC_`, 8 digits, `_00`), and `AP_` and 8 digits or all null; the digits are not checked against the common definitions of BS.2094 or the XML;
  - `wav.chna-track-index-out-of-range`: a track index beyond the channels of the `fmt ` chunk.
- The XML chunks (section 9): `wav.xml-chunk-repeated` for a second `axml`, `bxml` or `sxml` chunk, and `wav.adm-without-chna` when an `axml` chunk, or an uncompressed `bxml` chunk, holds the Audio Definition Model's root element `audioFormatExtended` (found by a scan of its first MiB) and the file has no `chna` chunk. XML is not parsed or checked, a `bxml` chunk of gzip is not decompressed, and ADM in both `axml` and `bxml` is not detected as such, since a compressed chunk is not scanned.
- `ubxt` and the XML structure for converting `bext` (section 11) are not read; a file named `.bw64` (section 12 asks readers to be tolerant of it) is detected by its bytes like any other, and `--as bw64` is an alias of `--as wav`.

## Channel layout

- The channel mask of WAVEFORMATEXTENSIBLE gives the speakers in the order of its bits (front left, front right, front center, LFE, back left, back right and so on up to the top speakers). The channels follow that order.
- Bits 18 and above are no WAVE speaker positions: Microsoft reserves them, and EBU Tech 3306-2009 section 3 assigns bits 23 to 30 (bitstreams, control samples and the stereo down mix). The analyzer counts each as a channel without a position (discrete); bit 31, SPEAKER_ALL, is not counted in the check.
- A mask and a channel count that disagree follow Microsoft's rule ("Multiple channel audio data and WAVE files", "Details about dwChannelMask"): with fewer channels than bits the extra, most significant bits are ignored, and with more the remaining channels are assigned to no speaker (discrete). The layout is built that way, and `wav.channel-mask-mismatch` is an information finding. A mask of 0 gives no layout.
- The sub-format GUID is read with its first three groups little-endian, as Windows stores it, also in a RIFX file; the format tag is its first four bytes read little-endian. Microsoft's documents say nothing of RIFX, and FFmpeg's `riffdec.c` reads the tag the same way.
- Without a mask, one channel is front center and two are front left and front right (`ksmedia.h`, `KSAUDIO_SPEAKER_MONO` and `KSAUDIO_SPEAKER_STEREO`). More channels have no layout.
- A layout is named (5.1, 7.1 and so on) where it matches a common arrangement.

## Encoder and writer detection

- The `ISFT` chunk of `LIST INFO` (Software) is a claim of the writer, with the weight a tag text has. It is shown as "Writer" with its source. An ID3v2 or APEv2 tag that names another writer gives `wav.writer-claim-differs`.
- The `bext` originator and the `iXML` text are shown but not used as evidence.
- No audio encoder is detected: the samples are not read, and a compressed stream's encoder is the codec parser's business.

## What is not covered

- The sub-chunks of `sxml`, the XML of the Audio Definition Model (its IDs against `chna`) and `ubxt`.
- IEC 61937 bursts of AC-3 or DTS inside PCM samples (the "SPDIF in WAV" case): the data is PCM to the analyzer. Only a DTS core stream written straight into the samples of a 16-bit stereo file is read (see Routes).
- The `CHAN`-style channel descriptions of other containers, and the contents of `levl`, `link`, `dbmd`, `qlty` and `r64m`.
- Decoding, and a check of the samples. A WAVE file is verified by nothing but its headers.
- sox was not used for the cross-check: FFmpeg writes the fixtures and ffprobe and MediaInfo read them.

## Tests and fixtures

See "WAV and AIFF" in [development.md](../development.md).

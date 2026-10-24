# WAV

WAVE files in a RIFF container (`.wav`, `.wave`): RIFF for little-endian integers, RIFX for big-endian ones and RF64 for sizes of 64 bits. There is no decoder; the audio is not decoded.

Sources: Microsoft's and IBM's Multimedia Programming Interface and Data Specifications 1.0 (RIFF, the WAVE form, `fmt `, `fact`, `data`, `cue `, `plst`, `LIST` with `INFO`, `adtl` and `wavl`, `smpl`, `inst`), Microsoft's WAVEFORMATEX and WAVEFORMATEXTENSIBLE documentation and `mmreg.h`/`ksmedia.h` identifiers, EBU Tech 3285 (`bext`), EBU Tech 3306 (RF64, `ds64`), ITU-R BS.2088 (`chna`, `axml`) and the iXML specification, with FFmpeg 6.1.1's `riffdec.c`, `wavdec.c` and `wavenc.c` as the reference reader.

## What is read

- The RIFF header: the chunk ID (`RIFF`, `RIFX`, `RF64`), the size and the form type `WAVE`.
- Every chunk is a node. A chunk has a 4-character ID, a 32-bit size, the data and, for an odd size, a pad byte. A chunk the parser does not know is named by its FourCC and shows its size.
- `fmt `:
  - WAVEFORMATEX: the format tag, channels, sample rate, average bytes per second, block alignment, bits per sample and the extra data;
  - WAVEFORMATEXTENSIBLE: the valid bits (or samples per block, or reserved), the channel mask and the sub-format GUID, which resolves to the format tag the sub-format stands for;
  - the extra fields of the compressed formats: Microsoft ADPCM (samples per block and the coefficient sets, the first 64 listed), IMA ADPCM and GSM 6.10 (samples per block), MPEG1WAVEFORMAT and MPEGLAYER3WAVEFORMAT.
- `fact` (sample length), `data` (the first one is the stream; further ones are `wav.data-repeated`) and `ds64` (the RF64 sizes and the table of larger chunks).
- `LIST`:
  - `INFO`: each text chunk by its name from the specification (`INAM` Title, `IART` Artist, `ISFT` Software and so on);
  - `adtl`: `labl`, `note`, `ltxt` and `file`;
  - `wavl`: `data` and `slnt` chunks.
- `bext` (EBU broadcast extension: description, originator, time reference, version, UMID and, from version 2, the loudness values), `cue ` (cue points), `plst` (playlist), `smpl` (sampler: manufacturer, MIDI unity note, loops), `inst` (instrument), `chna` (channel allocation of the Audio Definition Model), `iXML` and `axml` (XML as text), `CSET`, `JUNK` and `PAD ` (filler).
- `id3 ` and `ID3 `: the chunk's data is handed to the ID3 tag parser through `ParseTag`, so an ID3v2 tag in a WAVE file is shown as any other.
- `levl`, `link`, `dbmd`, `qlty` and `r64m` are named and sized; their contents are not read.
- At most 2,048 chunks are listed at one level; the rest are `wav.chunk-limit`. Lists inside chunks (cue points, loops, table entries) list at most 1,024 entries.
- LIST chunks nest at most 8 levels (`wav.list-nesting`).
- The samples are not read. The figures come from the headers.

## Sizes, placeholders and RF64

- A RIFF size or a `data` size of `0xFFFFFFFF` is a placeholder of a writer that could not seek back (FFmpeg writing to a pipe does this). The data then runs to the end of the file (`wav.riff-size-placeholder`, `wav.data-size-placeholder`). A `data` size of 0 followed by no chunk is treated the same way.
- In an RF64 file the 32-bit sizes of `0xFFFFFFFF` are resolved from `ds64`: the RIFF size, the `data` size, the sample count and a table of the other chunks that need 64 bits. A missing `ds64` is `wav.ds64-missing`, one that is not first `wav.ds64-not-first`, and sizes that disagree with the file `wav.ds64-riff-size-mismatch` and `wav.ds64-data-size-mismatch`.
- `DeclaredEnd` is the RIFF chunk's own end (for RF64 from `ds64`). It is what lets the host tell a tag after the chunk from a tag inside it; a size that is 0, `0xFFFFFFFF` or under 4 states no end.
- Bytes after the RIFF chunk are `wav.trailing-bytes`; a chunk after it that looks like part of the file is `wav.data-after-riff`. A chunk cut by the end of the file is `wav.chunk-past-end` and the file `wav.truncated`.
- A pad byte that is missing where the next chunk starts right after an odd chunk is `wav.pad-byte-missing`; the parser follows the layout that gives a valid chunk ID next. A nonzero pad byte is `wav.pad-byte-not-zero`.

## Reported

A WAVE file of linear PCM, IEEE float, A-law or mu-law reports a stream: codec, sample rate, channels, layout, bits per sample, the samples from the `data` size and the block alignment, the duration and the average bitrate. The statistics node lists them, with the container width where it differs from the valid bits, the sample format and the byte order.

- A compressed format (Microsoft ADPCM, IMA ADPCM, GSM 6.10, ATRAC, WMA, Vorbis in WAVE and so on) is named by its format tag. The stream reports the codec name, the rate and channels from the header, the duration from `fact` where there is one, and the bitrate from the header's average bytes per second. The coding is not analyzed (`wav.coding-not-analyzed`, info).
- A tag with no name in the table is `Other` and `wav.coding-unknown`.
- MPEG audio (tags 0x0050 and 0x0055), AC-3 (0x2000) and DTS (0x2001) data go to the codec parsers through `ParseStream` with codec IDs `mpeg-audio`, `ac-3` and `dts`. The routed parser's stream is the file's stream; `wav.format-differs-from-stream` is raised where the header's channels (plain WAVEFORMATEX only) or sample rate disagree with what the codec parser read.
- E-AC-3 written by FFmpeg uses an extensible header whose sub-format GUID is FFmpeg's own and not a format tag in the Microsoft base GUID; it is `Other` with `wav.coding-unknown` and is not routed.

## Findings on the format chunk

- A second `fmt ` is `wav.format-repeated`; a missing one `wav.no-format-chunk`; a `data` chunk before it `wav.data-before-format`.
- Zero channels, a zero sample rate and zero bits per sample for a linear format are `wav.channels-zero`, `wav.sample-rate-zero` and `wav.bits-per-sample-zero`.
- `wav.block-align-mismatch` and `wav.average-bytes-mismatch` compare the header's block alignment and average bytes per second with the values the channels, the rate and the width give.
- `wav.float-bits` (floating point other than 32 or 64 bits), `wav.companded-bits` (A-law or mu-law other than 8 bits).
- `wav.format-extra-size`, `wav.extensible-incomplete`, `wav.valid-bits-exceed-container` and `wav.channel-mask-mismatch` (a mask that names a different number of speakers than the channels; bits at 18 and above are not speakers).
- `wav.fact-missing` (info: a format that is not PCM or floating point needs a `fact` chunk), `wav.fact-samples-mismatch`, `wav.partial-sample-frame` (a data size that is not a whole number of sample frames), `wav.no-data-chunk`, `wav.no-audio`.

## Channel layout

- The channel mask of WAVEFORMATEXTENSIBLE gives the speakers in the order of its bits (front left, front right, front center, LFE, back left, back right and so on up to the top speakers). The channels follow that order; bits at 18 and above are not speakers, and channels past the last speaker are discrete.
- The layout is reported only when the mask names as many speakers as the stream has channels (`wav.channel-mask-mismatch` otherwise); a stream with a mask that does not fit has no layout.
- Without a mask, one channel is front center and two are front left and front right (`ksmedia.h`, `KSAUDIO_SPEAKER_MONO` and `KSAUDIO_SPEAKER_STEREO`). More channels have no layout.
- A layout is named (5.1, 7.1 and so on) where it matches a common arrangement.

## Encoder and writer detection

- The `ISFT` chunk of `LIST INFO` (Software) is a claim of the writer, with the weight a tag text has. It is shown as "Writer" with its source. An ID3v2 or APEv2 tag that names another writer gives `wav.writer-claim-differs`.
- The `bext` originator and the `iXML` text are shown but not used as evidence.
- No audio encoder is detected: the samples are not read, and a compressed stream's encoder is the codec parser's business.

## What is not covered

- BW64 (ITU-R BS.2088) as a form type; the `chna` and `axml` chunks of an ADM file are read as above.
- The uppercase spellings `FL32` and `FL64` of floating-point formats in other containers.
- IEC 61937 bursts of AC-3 or DTS inside PCM samples (the "SPDIF in WAV" case): the data is PCM to the analyzer.
- The `CHAN`-style channel descriptions of other containers, and the contents of `levl`, `link`, `dbmd`, `qlty` and `r64m`.
- Decoding, and a check of the samples. A WAVE file is verified by nothing but its headers.
- sox was not used for the cross-check: FFmpeg writes the fixtures and ffprobe and MediaInfo read them.

## Tests and fixtures

See "WAV and AIFF" in [development.md](../development.md).

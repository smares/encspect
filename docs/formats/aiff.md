# AIFF

AIFF and AIFF-C files (`.aif`, `.aiff`, `.aifc`): an IFF `FORM` chunk of the form type `AIFF` or `AIFC`, with big-endian integers. There is no decoder; the audio is not decoded.

Sources: Apple's "Audio Interchange File Format: AIFF, version 1.3" (1989) and the "Audio Interchange File Format AIFF-C" draft of 8/26/91 (`FVER`, the compression types, the Common Chunk's extension), with FFmpeg 6.1.1's `aiff.c`, `aiffdec.c` and `aiffenc.c` for the compression types the drafts do not list (`sowt`, `in24`, `in32`, `raw `, `fl32`, `fl64`, `alaw`, `ulaw`, `ima4`, `GSM `; of these FFmpeg 6.1.1 wrote `sowt`, `raw `, `fl32`, `fl64`, `alaw`, `ulaw` and `ima4` into the fixtures).

## What is read

- The FORM header: the chunk ID, the size and the form type.
- Every chunk is a node: a 4-character ID, a 32-bit size, the data and, for an odd size, a pad byte. A chunk the parser does not know is named by its ID and size.
- `COMM` (Common Chunk): channels, sample frames, sample size, the sample rate as an 80-bit extended number and, in AIFF-C, the compression type and its name.
- `SSND` (Sound Data Chunk): the offset and block size; the samples are not read.
- `FVER` (AIFF-C format version): the timestamp, which must be 2726318400 (23 May 1990), the version of the draft.
- `MARK` (markers), `INST` (instrument: base note, detune, gains, sustain and release loops), `COMT` (comments with a time stamp, a marker ID and text), `AESD` (audio recording), `MIDI` and `APPL` (application specific, with the signature).
- `NAME`, `AUTH`, `(c) ` and `ANNO`: text chunks.
- `ID3 ` and `id3 `: the chunk's data is handed to the ID3 tag parser through `ParseTag`.
- At most 2,048 chunks are listed (`aiff.chunk-limit`); tables inside chunks list at most 1,024 entries.

## Sizes and damage

- A FORM size of 0 or `0xFFFFFFFF` is the placeholder of a writer that could not seek back (`aiff.form-size-placeholder`); a `SSND` size of `0xFFFFFFFF` is the same for the sound data (`aiff.sound-size-placeholder`). The chunk then runs to the end of the file.
- `DeclaredEnd` is the FORM chunk's own end. A tag after it is the host's; the `ID3 ` chunk that taggers write last lies inside it.
- Bytes after the FORM chunk are `aiff.trailing-bytes`; a chunk after it `aiff.data-after-form`. A cut file is `aiff.truncated`; a chunk that runs past the end `aiff.chunk-past-end`.
- Pad bytes: `aiff.pad-byte-missing` and `aiff.pad-byte-not-zero`.
- A chunk ID outside printable ASCII is `aiff.chunk-id-invalid`; a chunk that has to be shorter than its fixed fields is `aiff.chunk-too-short`; a chunk of a kind the specification allows once, repeated, is `aiff.chunk-repeated`.

## Reported

A file with a Common Chunk reports a stream: codec, sample rate, channels, layout, bits per sample, sample frames, duration and average bitrate. The statistics node adds the compression type, the sample format and the byte order for the linear codings.

- The sample rate is read from the 80-bit number; infinity, not a number, zero and negative values are `aiff.sample-rate-invalid`, and the stream then has no duration.
- **Compression types.** `NONE` and `twos` (signed big-endian PCM), `sowt` (little-endian PCM), `in24` and `in32` (24 and 32 bits), `raw ` (unsigned 8 bits), `fl32` and `fl64` (floating point), `alaw` and `ulaw`, `ima4`, `GSM `, `ACE2`, `ACE8`, `MAC3` and `MAC6`. An unlisted type is `Other` with `aiff.compression-unknown`. A FORM of type `AIFF` has no compression type (its samples are `NONE`); an `AIFC` Common Chunk that ends with the sample rate is `aiff.compression-type-missing`.
- **Length.** For the codings that store samples one after another, the Common Chunk's sample frames are checked against the sound data: a count that the data cannot hold is clipped to what it holds (`aiff.sound-data-short`), a count of 0 with data is counted from the data (`aiff.sample-frames-zero`), and bytes beyond the stated frames with no block size to explain them are `aiff.sound-data-extra`.
- **Packed codings.** `ima4` counts 64-sample packets of 34 bytes for each channel in the Common Chunk; the duration is that count times 64 samples, as FFmpeg's `aiffdec.c` reads it (MediaInfo 26.01 reads the count as sample frames and reports a duration 64 times too short). For the other packed codings (GSM, ACE, MACE and unlisted types) the spec does not say what the count counts, so the stream has no length (`aiff.length-unknown`, info) and no bitrate.
- `aiff.sample-size-invalid` and `aiff.sample-size-mismatch` (a sample size that disagrees with the width the type fixes, such as 16 bits for `in24`).
- `aiff.no-common-chunk` (no stream), `aiff.no-sound-data`, `aiff.sound-offset-past-end`, `aiff.channels-zero`.
- `aiff.no-format-version` (AIFF-C without `FVER`) and `aiff.format-version-unknown`.
- Markers: `aiff.marker-count-mismatch`, `aiff.marker-id-repeated`, `aiff.marker-id-not-positive` and `aiff.marker-missing` (a loop or comment that refers to a marker ID the Marker Chunk does not hold). Comments: `aiff.comment-count-mismatch`.

## Channel layout

- One channel is front center, two are front left and front right and three are front left, front right and front center (specification, "Common Chunk").
- More channels give no layout: the specification lists two conventions for four channels that disagree on what the channels are, and one for six that places a "surround" channel nowhere.

## Encoder and writer detection

None. AIFF has no field for the writer; the text chunks (`ANNO`, `AUTH`) are shown as the file states them and are not evidence. An ID3v2 tag in the `ID3 ` chunk is read by the tag parser as any other, and its claims are weighed there.

## What is not covered

- Decoding, and any check of the samples.
- The contents of the packed codings (IMA, GSM, ACE and MACE blocks).
- The `CHAN` chunk (Core Audio channel descriptions) and other chunks Apple's tools add: they are listed by their ID and size.
- sox was not used for the cross-check.

## Tests and fixtures

See "WAV and AIFF" in [development.md](../development.md).

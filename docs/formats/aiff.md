# AIFF

AIFF and AIFF-C files (`.aif`, `.aiff`, `.aifc`): an IFF `FORM` chunk of the form type `AIFF` or `AIFC`, with big-endian integers. There is no decoder; the audio is not decoded.

Sources:

- Apple's "Audio Interchange File Format: AIFF, version 1.3" (1989) and the "Audio Interchange File Format AIFF-C" draft of 8/26/91 (`FVER`, the compression types of section 4.0 and the sample frames of the compressed codings in its appendix C).
- For the `CHAN` chunk: Apple's Core Audio Format Specification, "Channel Layout Chunk", "Channel Layout Tags", "Channel Bitmaps", "Channel Description" and "Label Codes for Channel Layouts" (<https://developer.apple.com/library/archive/documentation/MusicAudio/Reference/CAFSpec/CAF_spec/CAF_spec.html>), which gives the structure for CAF files. No Apple document read says that an AIFF file holds the same structure in a `CHAN` chunk; the analyzer reads it so because FFmpeg 6.1.1's muxer writes it (12 bytes: tag, bitmap, a count of 0 descriptions) and QuickTime's `chan` atom holds the same structure. Files written by Apple's own tools were not available, so their `CHAN` chunks (with descriptions, say) are not checked against a real file.
- For the compression types the drafts do not list (`sowt`, `in24`, `in32`, `raw `, `fl32`, `fl64`, `alaw`, `ulaw`, `ima4`): Apple's QuickTime File Format Specification (2001-03-01), "Sound Codec Formats Supported" (Table 3-3), whose sound sample description codes AIFF-C files use, and for the packet of `ima4` (64 samples in 34 bytes for each channel) Apple's Core Audio Format Specification, "Audio Description Chunk".
- `GSM `: no Apple document read lists it; it is a code FFmpeg 6.1.1's `aiff.c` reads. FFmpeg 6.1.1 wrote `sowt`, `raw `, `fl32`, `fl64`, `alaw`, `ulaw` and `ima4` into the fixtures; FFmpeg and MediaInfo are cross-checks, not sources of the format.

## What is read

- The FORM header: the chunk ID, the size and the form type.
- Every chunk is a node: a 4-character ID, a 32-bit size, the data and, for an odd size, a pad byte. A chunk the parser does not know is named by its ID and size.
- `COMM` (Common Chunk): channels, sample frames, sample size, the sample rate as an 80-bit extended number and, in AIFF-C, the compression type and its name.
- `SSND` (Sound Data Chunk): the offset and block size; the samples are not read.
- `FVER` (AIFF-C format version): the timestamp, which must be 2726318400 (23 May 1990), the version of the draft.
- `MARK` (markers), `INST` (instrument: base note, detune, gains, sustain and release loops), `COMT` (comments with a time stamp, a marker ID and text), `AESD` (audio recording), `MIDI` and `APPL` (application specific, with the signature).
- `NAME`, `AUTH`, `(c) ` and `ANNO`: text chunks.
- `CHAN` (Channel Layout Chunk): Core Audio's layout tag, channel bitmap and channel descriptions (label, flags, coordinates); see Channel layout below.
- `ID3 ` and `id3 `: the chunk's data is handed to the ID3 tag parser through `ParseTag`.
- Limits, so that a small file cannot ask for a large report:
  - at most 2,048 chunks are listed (`aiff.chunk-limit`) and tables inside chunks (markers, comments) list at most 1,024 entries;
  - the tree holds at most 250,000 nodes in all, as MP4's and Matroska's do: past that the rest of the FORM is one "Chunks Not Shown" node and is not read (`aiff.node-limit`, warning);
  - the references of loops and comments to markers, kept to find one the Marker Chunk does not hold, are limited to 65,536.
- A chunk ID is four printable characters from the space to the tilde, with no space before a printing character and none as its first character (a blank run is padding).
- A Pascal-style string (the compression name, the application name of `pdos` and `stoc`) is followed by its pad byte when its count is even, shown as the file has it.

## Sizes and damage

- A FORM size of 0 or `0xFFFFFFFF` is the placeholder of a writer that could not seek back (`aiff.form-size-placeholder`); a `SSND` size of `0xFFFFFFFF` is the same for the sound data (`aiff.sound-size-placeholder`). The chunk then runs to the end of the file.
- `DeclaredEnd` is the FORM chunk's own end. A tag after it is the host's; the `ID3 ` chunk that taggers write last lies inside it.
- Bytes after the FORM chunk are `aiff.trailing-bytes`; a chunk after it `aiff.data-after-form`. A cut file is `aiff.truncated`; a chunk that runs past the end `aiff.chunk-past-end`.
- Pad bytes: `aiff.pad-byte-missing` and `aiff.pad-byte-not-zero`.
- A chunk ID outside printable ASCII is `aiff.chunk-id-invalid`; a chunk that has to be shorter than its fixed fields is `aiff.chunk-too-short`; a chunk of a kind the specification allows once, repeated, is `aiff.chunk-repeated`.

## Reported

A file with a Common Chunk reports a stream: codec, sample rate, channels, layout, bits per sample, sample frames, duration and average bitrate. The statistics node adds the compression type, the sample format and the byte order for the linear codings.

- The sample rate is read from the 80-bit number; infinity, not a number, zero, negative values and values under 1 are `aiff.sample-rate-invalid`, and the stream then has no duration.
- **Compression types.** `NONE` and `twos` (signed big-endian PCM), `sowt` (little-endian PCM), `in24` and `in32` (24 and 32 bits), `raw ` (unsigned 8 bits), `fl32` and `fl64` (floating point), `alaw` and `ulaw`, `ima4`, `GSM `, `ACE2`, `ACE8`, `MAC3` and `MAC6`. An unlisted type is `Other` with `aiff.compression-unknown`. A FORM of type `AIFF` has no compression type (its samples are `NONE`); an `AIFC` Common Chunk that ends with the sample rate is `aiff.compression-type-missing`.
- **Length.** For the codings that store samples one after another, the Common Chunk's sample frames are checked against the sound data: a count that the data cannot hold is clipped to what it holds (`aiff.sound-data-short`), a count of 0 with data is counted from the data (`aiff.sample-frames-zero`), and bytes beyond the stated frames with no block size to explain them are `aiff.sound-data-extra`.
- **Packed codings.** The Common Chunk counts "the number of sample frames in the Sound Data Chunk" (AIFF-C, section 4.0), and for a compressed coding a sample frame is the unit of appendix C, for each channel:

  | Type | Bytes of a sample frame | Samples it holds |
  |---|---|---|
  | `MAC3` (MACE 3:1) | 2 | 6 |
  | `MAC6` (MACE 6:1) | 1 | 6 |
  | `ACE2` (ACE 2:1) | 1 | 2 |
  | `ACE8` (ACE 8:3) | 3 | 8 |
  | `ima4` | 34 | 64 |

  The length is the frames (the Common Chunk's count, cut to the frames the sound data holds) times the samples one holds, and the bitrate follows from the bytes of the sound data. The table of markers in appendix C maps the same ratios. MediaInfo 26.01 reads the count as sample frames and reports a duration that many times too short; FFmpeg 6.1.1 and 8.0.1 read MACE and `ima4` as the table says and have no ACE types. For `GSM ` and unlisted types no source read says what the count counts (QuickTime's specification gives a GSM block only as "typically 160 samples packed into 33 bytes"; FFmpeg takes the count for blocks), so the stream has no length (`aiff.length-unknown`, info) and no bitrate.
- `aiff.sample-size-invalid` and `aiff.sample-size-mismatch` (a sample size that disagrees with the width the type fixes, such as 16 bits for `in24`).
- `aiff.no-common-chunk` (no stream), `aiff.no-sound-data`, `aiff.sound-offset-past-end`, `aiff.channels-zero`. Findings about what a file holds are warnings, as in the other format parsers; an error is the analysis failing, the host's `parse.*`.
- `aiff.no-format-version` (AIFF-C without `FVER`) and `aiff.format-version-unknown`.
- Markers: `aiff.marker-count-mismatch`, `aiff.marker-id-repeated`, `aiff.marker-id-not-positive` and `aiff.marker-missing` (a loop or comment that refers to a marker ID the Marker Chunk does not hold). Comments: `aiff.comment-count-mismatch`.

## Channel layout

- A `CHAN` chunk states the layout. It has a layout tag, a bitmap and descriptions, and the tag says which gives the layout:
  - the tag `UseChannelDescriptions` (0): the channel descriptions, one for each channel, with a label such as Left or LFEScreen (the labels of the specification: 1 to 18 are the positions of the WAVE channel mask, 33 and 34 the rear surround pair, which makes the surround pair of 5 and 6 a side pair);
  - the tag `UseChannelBitmap` (0x10000): one channel for each bit of the bitmap, lowest first, in the order of the WAVE channel mask;
  - any other tag names an arrangement in its upper 16 bits and counts the channels in its lower 16: `MPEG_5_1_A` is front left, front right, front center, LFE, back left, back right. The tags are those of the specification (100 to 147) and the later ones of `CoreAudioBaseTypes.h` that QuickTime files use; the table is the one MP4's `chan` atom uses (`CoreAudioLayouts`, shared by the two parsers).
  - The speakers are written out in the stream's layout, in the order of the channels in the file, which the tag may put the center first in (`MPEG_3_0_B` is center, front left, front right).
- The number of channels the chunk states is checked against the Common Chunk's: the descriptions' count, the set bits of the bitmap or the lower 16 bits of the tag. A chunk that disagrees is `aiff.channel-layout-differs` (the Common Chunk's count and the layout of its channel count stand); one whose descriptions do not fit is `aiff.chan-descriptions-exceed-chunk`; a tag that is none of the table's is `aiff.layout-tag-unknown` (info) and gives no layout. A second `CHAN` chunk is `aiff.chunk-repeated`.
- Without a `CHAN` chunk: one channel is front center, two are front left and front right and three are front left, front right and front center (specification, "Common Chunk").
- More channels without a `CHAN` chunk give no layout: the specification lists two conventions for four channels that disagree on what the channels are, and one for six that places a "surround" channel nowhere.

## Encoder and writer detection

None. AIFF has no field for the writer; the text chunks (`ANNO`, `AUTH`) are shown as the file states them and are not evidence. An ID3v2 tag in the `ID3 ` chunk is read by the tag parser as any other, and its claims are weighed there.

## What is not covered

- Decoding, and any check of the samples.
- The contents of the packed codings (IMA, GSM, ACE and MACE blocks).
- Other chunks Apple's tools add: they are listed by their ID and size. The coordinates of a channel description are shown, not interpreted, and `CHAN` chunks written by Apple's tools were not checked.
- sox was not used for the cross-check.

## Tests and fixtures

See "WAV and AIFF" in [development.md](../development.md).

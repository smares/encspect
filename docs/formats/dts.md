# DTS

DTS Coherent Acoustics: core streams in raw `.dts` files and in [MP4](mp4.md), with the extensions of the core and the extension substreams of DTS-HD. Written from ETSI TS 102 114 V1.6.1.

## What is read

- **Core frames,** in the four forms of the core stream: 16-bit and 14-bit words, big and little endian. The specification describes the first; the others are read as FFmpeg and libdca define them, and both decode all four.
- **Frame header** (5.4.2), every field typed and described: frame type, deficit sample count, CRC flag, sample blocks, frame size, channel arrangement, sampling frequency, bit rate, the flags, extension type, LFE, encoder revision, source resolution, sum/difference flags and dialogue normalization.
- **Primary audio coding header** (5.4.3), for each channel: active subbands, high-frequency VQ start, joint intensity source, the code books and the quantizer selects with their adjustments.
- **Core extensions,** found as 6.4.2 and 6.2.4.2 say: by the sync word at the distance from the end of the frame that its size gives.
  - XCh: the frame header and the audio header of the extension channel.
  - X96: the frame header and the channel set header.
  - XXCH: the frame header with its CRC, and each channel set's header with its speaker mask.
- **Extension substreams** (7.5), alone or after a core frame:
  - the header with its CRC16, the static fields, the asset sizes and each audio asset descriptor (type, language, text, resolution, rate, channels, speaker masks, dynamic metadata, the coding mode and the sizes of the components);
  - each component by its sync word: the core (0x02B09261), XBR, XXCH, X96, LBR (its header with the sampling frequency, speaker mask and bit rate) and XLL (its common header with its CRC16);
  - substreams that hold only LBR or XLL, without a core.
- **Headers of the MP4 `ddts` box** (Annex E), compared with the frames.
- The subframes and the extensions' audio data are not decoded: sizes, sync words and CRCs are what the headers give.

## Frames and sync

- A sync word only starts a stream when a chain of frames follows it: two more units at the lengths the headers give, in the same form and at the same sampling frequency, or units that end exactly at the end of the data. A header must also be plausible (FSIZE of at least 96 bytes, at least 6 blocks, a defined sampling frequency).
- A unit is a core frame and the extension substreams after it (at the end of the core frame, or at the next 32-bit boundary after it, as 7.5.1 allows), or an extension substream alone.
- After sync, frames are followed by their lengths. A frame in which a new chain starts is cut short as damaged; one the data ends inside is truncated; anything else up to the next chain is a gap (leading junk, lost sync, trailing data). Embedded tags split the data as they do for other formats.
- Sniffing gives 93 for a chain at the start of the data (after zero bytes) and 91 for one further in.
  - At the start, 93 is above AC-3's 92, ADTS's 91 and MPEG audio's 90. The sync words differ in their first bytes (0x7F, 0xFE, 0x1F or 0xFF with 0x1F, 0x64 or 0x58 against AC-3's 0x0B and 0xFF with 0xFx), so no chain is two formats'.
  - Further in, 91 is just above MPEG audio's 90 for a stream anywhere in the first MiB, because MPEG audio's free format reads the 0xFFFF that ends a subframe, followed by zero bytes, as a chain of frames of its own. A DTS chain is a four-byte sync word, a valid header and two more frames at the lengths it gives, which junk and other formats' data do not make.
  - Both stay below the containers' 95 and 100 (an MP4 file holds DTS frames in its samples); `DtsTests` checks that every other fixture sniffs 0.
- Frame sizes of the 14-bit forms are the packed size times 8/14, rounded down to words (libdca's parse.c).

## Reported

- Codec by profile: "DTS", "DTS-ES" (XCh or XXCH in the core), "DTS 96/24" (X96 in the core), "DTS-HD High Resolution Audio" (XBR, XXCH or X96 in a substream), "DTS Express" (LBR) and "DTS-HD Master Audio" (XLL). The specification names none; the names are FFmpeg's (dca_parser.c), and the profile node cites its evidence.
- The sampling frequency of the core, with the extension's frequency (X96) as a separate field. Channels and layout, bits per sample, samples, duration, nominal and actual bitrate with series (each unit's bitrate and each second's), `IsVbr`, `IsLossless` and `HasExtensionSubstream`.
- The encoder revision (VERNUM) as a field only.
- Statistics per stream: frames of each kind, termination frames, extensions, dialogue normalization, dynamic range coefficients, time stamps, auxiliary data, CRC flags and failures, the coding components and the audio assets.

## Header CRCs

- **Verified:** the CRC16 of the extension substream header, the XXCH frame header and channel set headers, the XBR and X96 frame headers (in a substream) and the XLL common header (Annex B).
- **Not verified:** the CRC words that a core frame with CPF set carries (HCRC, AHCRC, SICRC). 5.4.2 says the check shall not be applied; the words are read, shown as not verified with that reason, and the flag is an info finding (`dts.crc-present`).

## Channel layout

- From AMODE (Table 5-4) and LFF. Up to five channels come from the core, the LFE channel from the flag.
- XCh adds a back center channel; XXCH adds the speakers its channel sets' masks name (Table 6-22); an asset of an extension substream states its own speaker activity mask (Table 7-10).
- A lone surround pair is the back pair; with a rear pair, the surround pair is the side pair, as for other formats. A user defined arrangement (AMODE 16 and above) has discrete channels of the number the core codes and an info finding.

## Encoder detection

- The frames name no encoder: VERNUM is the revision of the encoder software, shown as a field. Claims from tags (an ID3v2 TSSE frame, an APEv2 Encoder item) and from a container name one with low confidence, as written. A name FFmpeg's muxer writes (Lavf) names none.

## MP4

- The `dtsc`, `dtsh`, `dtsl` and `dtse` sample entries and the `mp4a` entry with the esds object type 0xA9 (FFmpeg's muxer writes DTS this way, without a `ddts` box) route their samples to the DTS codec parser. A sample holds a core frame and its extension substream.
- The `ddts` box is read field by field and compared with the first frames: sampling frequency, bit rates, frame duration, stream construction (Table E-2), LFE, core layout and size, and multiple assets. A box that disagrees is a warning (`dts.config-differs`), as are reserved bits that are set (`dts.config-reserved`) and a box that ends inside a field (`dts.config-truncated`). Bytes after the samples' frames are `dts.sample-not-frame`.

## Findings

- **Framing:** `dts.leading-junk`, `dts.sync-lost`, `dts.trailing-data`, `dts.frame-truncated`, `dts.frame-damaged`, `dts.header-invalid`, `dts.stream-changed`, `dts.sample-not-frame`.
- **Core header:** `dts.reserved-value`, `dts.user-defined-arrangement`, `dts.rate-code` (RATE 25 to 28, 30 and 31, which FFmpeg and libdca list), `dts.revision-incompatible`, `dts.crc-present`, `dts.frame-type`, `dts.block-count`, `dts.subframe-count`, `dts.rate-exceeded`, `dts.channel-count`, `dts.subband-count`, `dts.joint-source`, `dts.header-overrun`.
- **Extensions:** `dts.extension-missing`, `dts.extension-size-legacy` (the size field gives the distance itself, which 6.4.2's note accepts).
- **Extension substreams:** `dts.crc-mismatch`, `dts.substream-overrun`, `dts.asset-descriptor-overrun`, `dts.component-sync`, `dts.coding-mode-auxiliary`.
- **Other:** `dts.encoder-claim`, `dts.config-differs`, `dts.config-reserved`, `dts.config-truncated`.
- Findings of a kind are counted once per file, pointing at the first case.

## Limits

- Each unit keeps a compact record of the bytes the headers read; the nodes are built from it 1,024 at a time. A stream of 256-byte frames keeps about 80 bytes per frame, and a unit with an extension substream about 215 (MemoryTests).
- Sync search is linear: the complexity tests hold 24 MiB of core headers that chain nowhere, and of sync words with invalid headers, to a few seconds.

## Not covered

- The subframes (side information, scale factors, samples) and the audio data of the extensions: no entropy decoding. A damaged core frame without CRC words shows no finding unless its header is damaged; decoders such as FFmpeg cannot tell either (see `docs/development.md`).
- DTS:X and IMAX metadata, the core's optional information (time stamp, auxiliary data) beyond its flags, and the XLL channel set headers and navigation (the common header is read).
- DTS-HD files (`.dtshd`) with their own `DTSHDHDR` chunks: raw streams of the same frames are read, the chunk container is not.

# DTS

DTS Coherent Acoustics: core streams in raw `.dts` files and in [MP4](mp4.md), with the extensions of the core and the extension substreams of DTS-HD. Written from ETSI TS 102 114 V1.6.1.

## What is read

- **Core frames,** in the four forms of the core stream: 16-bit and 14-bit words, big and little endian. The specification describes the first; the others are read as FFmpeg and libdca define them, and both decode all four.
- **Frame header** (5.4.2), every field typed and described: frame type, deficit sample count, CRC flag, sample blocks, frame size, channel arrangement, sampling frequency, bit rate, the flags, extension type, LFE, encoder revision, source resolution, sum/difference flags and dialogue normalization.
- **Primary audio coding header** (5.4.3), for each channel: active subbands, high-frequency VQ start, joint intensity source, the code books and the quantizer selects with their adjustments.
- **Core extensions,** found as 6.4.2 (XCh) and 6.2.4.2 (X96) say, by the sync word at the distance from the end of the frame that its size gives, and XXCH (6.5.2) by its sync word with a header size that fits and a CRC16 that holds, or channel set sizes that lead to the end of the frame.
  - XCh: the frame header and the audio header of the extension channel.
  - X96: the frame header and the channel set header.
  - XXCH: the frame header with its CRC16, and each channel set's header with its speaker mask. A header whose CRC16 fails is still the extension's where its channel set sizes lead to the end of the frame; the failure is `dts.crc-mismatch`, not a missing extension.
- **Extension substreams** (7.5), alone or after a core frame:
  - the header with its CRC16, the static fields, the asset sizes and each audio asset descriptor (type, language, text, resolution, rate, channels, speaker masks, dynamic metadata, the coding mode and the sizes of the components);
  - each component by its sync word: the core (0x02B09261), XBR, XXCH, X96, LBR (its header with the sampling frequency, speaker mask and bit rate) and XLL (see below);
  - substreams that hold only LBR or XLL, without a core.
- **The lossless extension, XLL** (clause 8; the article `dts-lossless-extension`), from the sync word 0x41A29547 on:
  - the common header (8.3.1, Table 8-1) with its CRC16: version, header size, frame size, number of channel sets, segments and samples in a segment, the width of the sizes in the navigation index, the band data CRC mode, the scalable least significant bits flag and the width of the channel masks;
  - each channel set sub-header (8.3.2, Table 8-4) with its CRC16: channels, the residual channel encoding, source PCM resolution and storage bit width, sampling frequency (Table 8-5), the interpolation factor (Table 8-6), the replacement set and whether it is the active one (Table 8-7), whether the set is a primary set, whether it is hierarchical, whether downmix coefficients are embedded and whether the downmix is already done, the downmix type (Table 8-8), the number of downmix coefficients (not their values), the channel mask or the polar positions, and the number of frequency bands above 96 kHz. The prediction and decorrelation parameters of the bands and the mapping coefficients of sets that are not loudspeaker feeds are shown as bytes;
  - the navigation index (8.3.3, 8.4.2) with its CRC16: one size for each frequency band, segment and channel set that has the band, which add up to the band data;
  - the band data, as a node with its size, not decoded;
  - the extension data after the band data (see DTS:X below).
- **Headers of the MP4 `ddts` box** (Annex E), compared with the frames.
- The subframes and the extensions' audio data are not decoded: sizes, sync words and CRCs are what the headers give.

## Frames and sync

- A sync word only starts a stream when a chain of frames follows it: two more units at the lengths the headers give, in the same form and at the same sampling frequency, or units that end exactly at the end of the data. A header must also be plausible (FSIZE of at least 96 bytes, at least 6 blocks, a defined sampling frequency).
- A unit is a core frame and the extension substreams after it, or an extension substream alone. 7.5.1 puts every sync word on a 32-bit boundary and lets one to three null bytes align it:
  - a substream after a core frame or another substream is found at its end or at the next 32-bit boundary of the unit; one right after the end, off the boundary, is kept in the unit but not read, with `dts.substream-misaligned`, because FFmpeg's decoder looks at the boundary only (dcadec.c, `FFALIGN(frame_size, 4)`) and plays such a stream as its core. MediaInfo reads the substream there;
  - null bytes that bring the next unit's sync word to the boundary belong to the unit, also at the end of the data;
  - a substream after a core frame needs the right header CRC16, or to be whole and followed by a unit that passes the same test or by the end of the data, so a damaged header is reported and a sync word in junk is not taken for a substream. One that starts a unit needs the CRC16 always;
  - the extension substreams of the 14-bit forms are not looked for.
- After sync, frames are followed by their lengths. A frame in which a new chain starts is cut short as damaged; one the data ends inside is truncated; anything else up to the next chain is a gap (leading junk, lost sync, trailing data). Embedded tags split the data as they do for other formats.
- Sniffing gives class 93 for a chain at the start of the data (after zero bytes) and 50 for one further in, with the chain's start and its units (an extension substream that starts a unit has passed its header CRC and counts as verified). The host ranks formats by class, then the earlier start (see "Format detection" in the plan).
  - At the start, 93 is above AC-3's 92, ADTS's 91 and MPEG audio's 90. The sync words differ in their first bytes (0x7F, 0xFE, 0x1F or 0xFF with 0x1F, 0x64 or 0x58 against AC-3's 0x0B and 0xFF with 0xFx), so no chain is two formats'.
  - Further in, 50 is below MPEG audio's 90, ADTS's 91 at the start of the data, Ogg's 60, Musepack's 60 and WavPack's 60, as AC-3's is, so a DTS stream in the junk of another format (an MP3 file with DTS appended) does not take its file. MPEG audio does not take DTS data with junk in front: it scores a free-format chain whose frames do not fit their headers, or in Layers I and II have no CRC that holds, which a header-like 0xFFFF under a run of zero bytes does not, 30 instead of 90. That is below every other format's chain; MPEG audio still reads such a chain when nothing else claims the file.
  - Both stay below the containers' 95 and 100 (an MP4 file holds DTS frames in its samples); `DtsTests` checks that every other fixture sniffs 0.
- The sync search spends a bounded amount of CRC work on extension substream candidates (a start allowance and a few bytes for every byte scanned), so sync words 16 bytes apart that state headers of 4,096 bytes cost linear time (ComplexityTests).
- A frame ends on a whole 16-bit word in the file: the little endian form rounds FSIZE + 1 up to an even number of bytes, the 14-bit forms take the words that hold FSIZE + 1 times 8 bits at 14 bits each, rounded up (FFmpeg converts (size + 1) / 2 words; libdca rounds the 14-bit count down and finds the next sync word a word later). A frame of 1,000 bytes takes 1,144 bytes in a 14-bit form.
- 14-bit sync words are recognized by the 28 bits of the sync word and the four that finish it, not by FTYPE and SHORT as libdca and FFmpeg's parser also ask: the specification (5.3) defines termination frames for the core stream whatever its form, so they count in the 14-bit forms as in the others, and a frame type that disagrees with SHORT is `dts.frame-type`. FFmpeg's parser and libdca find no 14-bit termination frame, and FFmpeg's decoder rejects termination frames in every form (see `termination-*` in the manifests).

## Reported

- Codec by profile: "DTS", "DTS-ES" (XCh or XXCH in the core), "DTS 96/24" (X96 in the core), "DTS-HD High Resolution Audio" (XBR, XXCH or X96 in a substream), "DTS Express" (LBR, alone or with a core) and "DTS-HD Master Audio" (XLL). The specification names none; the names are FFmpeg's (dca_parser.c, dcadec.c), written out in full, and the profile node cites its evidence. The first asset of the first extension substream names the stream, and LBR comes before XLL, as in FFmpeg.
- The sampling frequency the stream delivers, the highest the core and the extensions state (96 kHz with X96 or a 96 kHz asset), as the stream's sample rate, with its samples counted at it; the core's frequency stays its own statistic ("Sampling Frequency") beside "Extension Sampling Frequency". ffprobe takes the rate of a core with an extension substream from the core until its decoder has decoded the extension; the lossless fixtures carry decodable silent XLL frames, so it reads the extension's rate, and the cross-check names the fixtures whose extension it cannot decode. Channels and layout, bits per sample, samples, duration, nominal and actual bitrate with series (each unit's bitrate and each second's), `IsVbr`, `IsLossless` and `HasExtensionSubstream`.
- For a stream whose first asset has an XLL component with every channel set sub-header read and its CRC16 holding, the lossless layer's figures: the bit depth is the largest source PCM resolution of its channel sets (the stream's bits per sample, and "Lossless Source PCM Resolution" in the statistics, with "Lossless Storage Bit Width"), the sampling frequency is the largest of the sets', and the channel layout is the union of their channel masks, which replaces the layout of the descriptor and the core. The statistics also give the number of channel sets and of channels. A set whose fields contradict each other (a resolution beyond the width, a mask of another number of speakers than channels) is not used.
- The encoder revision (VERNUM) as a field only.
- Statistics per stream: frames of each kind, termination frames, extensions, dialogue normalization, dynamic range coefficients, time stamps, auxiliary data, CRC flags and failures, the coding components and the audio assets.

## Header CRCs

- **Verified:** the CRC16 of the extension substream header, the XXCH frame header and channel set headers, the XBR and X96 frame headers (in a substream), and XLL's common header, channel set sub-headers and navigation index (Annex B). A unit with a failing one counts in "Frames Failing a CRC".
- **Not verified:** the CRC words that a core frame with CPF set carries and the parser reads: HCRC (5.4.2), AHCRC (5.4.3) and, for X96, AHCRC96 (6.2.4.4: "will not be used"). 5.4.2 and 5.4.3 say the check shall not be applied; the words are shown as not verified with that reason, and the flag is an info finding (`dts.crc-present`). SICRC (5.5.1, same wording) lies in the subframes, which are not read.

## Channel layout

- From AMODE (Table 5-4) and LFF. Up to five channels come from the core, the LFE channel from the flag. AMODE 10 to 15 (4/2, 3/2 with overhead, 3/3, 5/2, 4/4, 5/3) are named by the table's speaker lists; FFmpeg and libdca reject them, so the `amode-10` to `amode-15` fixtures rest on the table alone.
- XCh adds a back center channel; XXCH adds the speakers its channel sets' masks name (Table 6-22); an asset of an extension substream states its own speaker activity mask (Table 7-10).
- A lone surround pair is the back pair; with a rear pair, the surround pair is the side pair, as for other formats. A user defined arrangement (AMODE 16 and above) has discrete channels of the number the core codes and an info finding.

## Channel masks of the lossless extension

- A channel set's mask has one bit for each speaker, in the order of Table 6-22 (the mask of XXCH), which is how FFmpeg's `dca_xll.c` reads it. The specification points to its clause C.8 for the bits, which in V1.6.1 is about entropy coding, so the order rests on FFmpeg and on the real files below: the DTS-HD Master Audio 7.1 sample of the FFmpeg archive has a 5.1 set with the mask 0x627 (center, left, right, LFE and the side pair) and a set with 0x180 (the rear pair), which is the 7.1 layout ffprobe and MediaInfo report.
- The number of frequency bands follows Table 8-4: with a sampling frequency above 96 kHz, 4 bands when `bXtraFreqBands` is set and 2 when it is clear. The text of 8.3.2 and 8.5.1 describes the counts otherwise (1 or 2 for a set up to 192 kHz when clear), and 8.2.5 gives 2 for 192 kHz; FFmpeg reads 2 and does not decode a set with the flag.
- Downmix coefficients are counted as FFmpeg's `parse_dmix_coeffs` does: a primary set has a row for each channel its downmix type mixes to (1, 2, 2, 3, 3, 4 and 4 for types 0 to 6), a non-primary set a row for each channel of the sets before it in the hierarchy, each row with a scale; 9 bits each. The real 7.1 sample's 12 and 18 coefficients confirm it.
- The navigation index has one size for each frequency band, segment and channel set that has the band, as the text of 8.4.2 and FFmpeg say; the pseudo-code in 8.4.2 also reads a total for each segment, which no real file has.

## DTS:X

- No specification describes DTS:X; the analyzer implements the test of FFmpeg 6.1.1's `dca_xll.c` and reports what MediaInfo 24.01's `File_Dts.cpp` reads next to it. From the end of the band data (the sum of the navigation index's sizes behind it) the position is rounded up to 32 bits counted from the XLL sync word; if the frame has more bytes than that, the word there is read. 0x02000850 is DTS:X; 0xF14000D0 and 0xF14000D1 are DTS:X IMAX to FFmpeg (it ignores the lowest bit). MediaInfo reads 0x02000850 and 0xF14000D1 as DTS:X and 0xF14000D0 as IMAX (with DTS:X also set, "reported that IMAX is detected as X on legacy X decoders"), so a stream with 0xF14000D1 is IMAX to FFmpeg and plain DTS:X to MediaInfo.
- A stream with the word in any frame of its first asset has the profile "DTS-HD Master Audio + DTS:X" or "DTS-HD Master Audio + DTS:X IMAX", FFmpeg's "DTS-HD MA + DTS:X" and "DTS-HD MA + DTS:X IMAX" written out, and the statistics `HasDtsX` and `HasDtsXImax` (FFmpeg's two cases). The word is a node under "Extension Data" in the lossless frame (other bytes there are "Data After Band Data"). It is read only when every channel set is accepted (FFmpeg refuses a set with a failing CRC16, a source resolution beyond its storage width or a channel mask of another number of speakers than channels; MediaInfo 24.01 still reads the word after such a set, which the analyzer does not follow) and the navigation index's CRC16 holds, as FFmpeg returns before the word otherwise; a band data size larger than the frame (its stored value is the frame size or more) puts the band data's end past the frame, so no word is found, and only with 4 bytes left in the frame (FFmpeg reads it with 1 to 3 as well). The navigation index is limited as FFmpeg's is: 1,024 entries counted as the most bands times the segments times the sets. LBR, which FFmpeg's decoder names first, keeps the profile "DTS Express".
- The object audio behind the word is neither described by a source nor read: `HasObjectAudio` stays false, and the report says nothing about objects.

## Encoder detection

- The frames name no encoder: VERNUM is the revision of the encoder software, shown as a field. An ID3v2 TSSE frame names the encoder with low confidence, as written (`dts.encoder-claim` for a second one that differs).
- The writer is resolved apart, from the claims of the writer role: the BUILDVER chunk of a [.dtshd file](dtshd.md), an APEv2 Encoder item and ffmpeg's muxer version (Lavf), with low confidence; two that name different software are `dts.writer-claim`.

## MP4

- The `dtsc`, `dtsh`, `dtsl` and `dtse` sample entries and the `mp4a` entry with the esds object type 0xA9 (FFmpeg's muxer writes DTS this way, without a `ddts` box) route their samples to the DTS codec parser. A sample holds a core frame and its extension substream.
- The `ddts` box is read field by field and compared with the first frames: sampling frequency, bit rates, frame duration, stream construction (Table E-2), LFE, core layout and size, and multiple assets. A box that disagrees is a warning (`dts.config-differs`), as are reserved bits that are set (`dts.config-reserved`) and a box that ends inside a field (`dts.config-truncated`). Bytes after the samples' frames are `dts.sample-not-frame`.

## Findings

- **Framing:** `dts.leading-junk`, `dts.sync-lost`, `dts.trailing-data`, `dts.frame-truncated`, `dts.frame-damaged`, `dts.header-invalid`, `dts.stream-changed`, `dts.sample-not-frame`.
- **Core header:** `dts.reserved-value`, `dts.user-defined-arrangement`, `dts.rate-code` (RATE 25 to 28 and 30, which FFmpeg and libdca list), `dts.rate-lossless` (RATE 31, an info finding: 5.6 uses it to select the lossless step-size table), `dts.revision-incompatible`, `dts.crc-present`, `dts.frame-type`, `dts.block-count`, `dts.subframe-count`, `dts.rate-exceeded`, `dts.channel-count`, `dts.subband-count`, `dts.joint-source`, `dts.header-overrun`.
- **Extensions:** `dts.extension-missing`, `dts.extension-size-legacy` (the size field gives the distance itself, which 6.4.2's note accepts).
- **Alignment:** `dts.substream-misaligned`.
- **Extension substreams:** `dts.crc-mismatch`, `dts.substream-overrun`, `dts.asset-descriptor-overrun`, `dts.component-sync`, `dts.coding-mode-auxiliary`.
- **Lossless extension:** `dts.lossless-inconsistent` (a resolution beyond the storage width, a channel mask of another number of speakers than channels, a navigation index of more than 1,024 entries or whose sizes end past the frame); `dts.crc-mismatch`, `dts.reserved-value` (downmix type 7, an XLL version above 1), `dts.substream-overrun` and `dts.header-overrun` also report the sub-headers and the navigation index.
- **Other:** `dts.encoder-claim`, `dts.config-differs`, `dts.config-reserved`, `dts.config-truncated`.
- Findings of a kind are counted once per file, pointing at the first case.

## Limits

- Each unit keeps a compact record of the bytes the headers read; the nodes are built from it 1,024 at a time. A stream of 256-byte frames keeps about 80 bytes per frame, and a unit with an extension substream about 215 (MemoryTests).
- Sync search is linear: the complexity tests hold 24 MiB of core headers that chain nowhere, of sync words with invalid headers, and 32 MiB of extension substream headers that fail their CRC16, to a few seconds.

## Not covered

- The subframes (side information, scale factors, samples) and the audio data of the extensions: no entropy decoding (planned: entropy pass). A damaged core frame without CRC words shows no finding unless its header is damaged; decoders such as FFmpeg cannot tell either (see `docs/development.md`).
- DTS:X beyond the sync word of its extension data, and the prediction, decorrelation and entropy parameters of the XLL bands, the band data itself and the mapping coefficients of channel sets that are not loudspeaker feeds. An XLL frame that does not start at the start of its component (peak bit rate smoothing carries one over several access units, 7.5.3.4) is reported as missing its sync word; the real samples used here start every frame at its component.
- The core's optional information (time stamp, auxiliary data) beyond its flags (planned: entropy pass, as DTS coding statistics).
- DTS-HD files (`.dtshd`) are read by their own parser, see [dtshd.md](dtshd.md).

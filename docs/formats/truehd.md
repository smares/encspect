# Dolby TrueHD and MLP

Raw Dolby TrueHD files (`.thd`) and Meridian Lossless Packing files (`.mlp`): access units back to back. The specification Encspect follows is the clean-room description in [the format description](../design/truehd-format.md); the bit layouts, tables and limits are there and are not repeated here.

## What is read

- Every access unit:
  - the 4-byte header: the check nibble, the length in words and the input timing;
  - the major sync where there is one: sync word, stream type, format info (sampling rate, bit depth, channel arrangement or flags), signature, flags, variable-rate bit, peak data rate, substream count, extended substream info, substream info, channel meaning, the extension of a TrueHD sync, and the CRC-16;
  - the substream directory: for each substream the extra-word bit, the non-restart bit, the check data bit, the reserved bit and the end in words; the parity of the header and the directory;
  - each substream of the first three (the fourth is not read, see "Dolby Atmos"; its last two bytes are shown as the stored parity byte and CRC-8, not compared): its blocks, with the restart header (sync, noise type, channels, noise shift and seed, block size check flag, lossless check, channel assignment and CRC-8), the decoding parameters (block size, matrices, output shifts, quantizer step sizes, and for each channel the FIR and IIR filters, the Huffman offset, the codebook and the LSB count), the padding, the end of stream marker, the parity byte and the CRC-8.
- The residuals are walked: every bit of a block's data is read, so a block whose codewords or length break the syntax is found without decoding.
- Every unit is an entry of the tree, up to 2,097,152 entries (units and skipped stretches); the units past that are read, checked, decoded and counted, and have one node that counts them (`Counted Access Units`, `truehd.units-limit`). The first 512 units (within 4 MiB of their bytes) keep their bytes, so that their blocks are read again for the nodes when the unit is opened; the units with a major sync after them keep their header, major sync, directory and the ends of their substreams (up to 100,000 of them within 32 MiB), and the others their entry alone. A finding that points into a unit whose detail is not kept points to the unit. See "Memory".

## Units and resync

- A file is taken for TrueHD or MLP where a major sync whose CRC-16 holds starts a unit that holds it, in the first MiB of the data, and the units that follow it by their length words are consistent (the same structure as the decoder reads) for at least two units or to the end of the data. The detection class is 94 where the units start at the beginning of the data (or the units before the sync chain to it) and 60 otherwise.
- Units before the first major sync (`TrueHD.raw`, cut from a longer stream, begins with 13) are listed when their length words chain to the sync (`truehd.units-before-major-sync`, info); their substreams are not read, since the substream count and the sampling rate are not known yet. Otherwise those bytes are skipped (`truehd.data-skipped`).
- A unit is accepted where its header and directory hold and the parity nibble is right, or where its structure breaks but another unit follows it by its length word (or the data ends). Where neither holds, the bytes are skipped up to the next major sync unit whose sync checks out (`truehd.data-skipped`), and the units after the gap cannot be read until a major sync (`truehd.units-after-damage`).
- A unit cut by the end of the data is `truehd.unit-cut`. A unit whose major sync has a CRC-16 that differs (`truehd.major-sync-crc-mismatch`) or fields that are invalid (`truehd.major-sync-invalid`) cannot be read, and the units after it wait for the next major sync (`truehd.units-after-damage`).
- The units after an end of stream marker need a new major sync (`truehd.units-after-end-of-stream`, info), as in FFmpeg.
- What the analyzer leaves aside is not damage: a major sync that gives the two channel groups of an MLP stream different sampling rates, a negative output shift, and a last matrix channel above 5 with noise type 0. The unit is not read (for the major sync, none of its substreams is walked, decoded or counted), `truehd.unsupported` says so as information, and so do the units after it that carry no major sync, up to the next major sync that is read; they are not `truehd.units-after-damage`. A stream none of whose major syncs is read has no stream info and no verification; the stream info is that of the first major sync that is read.
- A major sync that changes the format, the sampling rate or the substream count in the middle of the data is `truehd.stream-parameters-change`, once for all of them. The decoder does not follow such a change: the units from that sync on are not handed to it (until a sync with the parameters it was opened with comes back), the verification stays unknown, and the duration counts each stretch at its own sampling rate; the samples are the total of all stretches. A change of the channel assignments or the bit depth alone is followed while the highest substream outputs the channel count the decoder was opened with.

## Reported

- the sampling rate (the first major sync's), bits per sample (the stream's, 16, 20 or 24), channels and layout of the highest presentation read, the samples (each unit's, less those an end of stream marker drops), the duration and the average bitrate, which counts the bytes of the units;
- the bitrate per run of 16 units and per second as series, the smallest and largest run and a distribution; the last run, if it holds fewer than 16 units, is in the series and the average but not in the smallest, largest, minimum, maximum and the distribution, unless it is the only one; the bitrate mode is variable where the run bitrates differ (`IsVbr`);
- statistics: the units and major syncs, the substreams and the presentations, whether the stream signals Dolby Atmos (`SignalsAtmos`), the restart headers, the checksums that held and failed (a substream of which the parity byte and the CRC-8 both differ counts once), the stored lossless checks, and the shortest and longest unit.

## Channel layout

- TrueHD: the channel flags of the major sync (the 2-, 6- and 8-channel assignments) name the speakers of each presentation. The layout of the highest substream read is the list of the presentation the channel count selects.
- MLP: the arrangement of the major sync (a table of 21 arrangements) names the channels.
- Where the restart header's channel assignment places two matrix channels on one position, or a channel count has no list in the sync, the channels are reported in the order of the matrix channels (`truehd.channel-assignment`, info).

## Checksums

Six checksums are declared: three cover headers, two the coded data and one, for each presentation, the decoded audio:

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| major sync CRC-16 | the major sync | never |
| unit parity nibble | the unit header and the directory | never |
| restart header CRC-8 | the restart header | never |
| substream parity byte | the substream's bytes | every substream has check data, no gap, no unreadable or cut unit |
| substream CRC-8 | the substream's bytes | the same |
| lossless check (one per presentation) | the decoded audio of the substream since its previous restart header | see "Decoding" |

- A stream is verified when every checksum it stores was compared, none failed, and at least one compared checksum covers all the audio ([`encspect --learn verification`](../cli.md)).
- The lossless check can only be compared after decoding. Without it, a stream whose coded-data checksums all hold and cover all the audio is verified from those alone; the verdict with decoding adds the lossless checks.
- The substream parity byte and CRC-8 are counted for each of the first three substreams of a unit that has check data; the fourth substream of an Atmos stream is neither compared nor counted, and the tree shows its last two bytes without a verdict.

## Decoding

- The decoder (`Encspect.Formats.TrueHd.Decoding`, IDs `truehd` and `mlp`) runs when the front end asks for it: `--verify` on the command line, "Decode Audio" in the detail window (which also measures the audio). The tree is the same either way.
- It reads no syntax of its own where the structure pass lent buffers: the walk stores the residuals and the bypassed bits, and the decoder reads the parameters again with the same reader (the syntax is one piece of code) and adds the prediction filters, the matrices, the noise, the output shifts and the lossless check. Where nothing was lent it reads the blocks itself.
- Decoding is bit exact: for every fixture that FFmpeg reads as the manifest does (the encoder-written files and the hand-built ones whose fault leaves the audio alone), the samples equal FFmpeg's decoder's, checked sample for sample in the cross-check job against FFmpeg 8.0.1 (the manifests' digests were made with 6.1.1; both give the same). For the three real files the cross-check job checks the lossless check of every presentation, ffprobe's and MediaInfo's fields; the samples were also compared with FFmpeg 6.1.1's and 8.0.1's, once and locally, with a throwaway harness over the host's public API (0 samples differ), which the job does not repeat.
- The output is the highest substream read, at the stream's bit depth (16 or 24 bits; a 20-bit stream is output as 24 bits, as FFmpeg passes it, so no bit of a sample is dropped), with the channel order of the layout. Lower substreams are decoded too (their channels feed the higher one) and their presentations' lossless checks are compared.
- The lossless check of each presentation is compared at its next restart header. `truehd.lossless-check-failed` (a warning on the restart header) names the first substream that differs; the host reports the findings of one code once with a count, so where several substreams differ the message names the first and the count the rest, and the verification lists each. The first restart header and the first after an end of stream marker have nothing to compare, and the output after the last restart header is never compared, so the checks together cover the output between a substream's first and last restart header.
- No check is compared across a break in the stream: the check at a major sync that changes the stream's parameters closes an interval that spans two streams, so it is not counted as stored; the checks inside a stretch the decoder did not decode (units not handed to it, or units it could not decode) are counted as stored and not compared, and the stream stays unverified.
- A unit the decoder cannot decode is `decode.frame-invalid`, once with a count: a unit whose highest substream outputs another channel count than the decoder was opened with, or whose syntax breaks in the decoder's reading. The decoder then drops its state (histories, noise registers, lossless accumulators), as it does after a gap, and decodes again from the next major sync it can follow. A unit with a major sync after a gap is decoded after a reset. A unit without one after a gap is not read by the analysis at all (`truehd.units-after-damage`), and a unit after an unreadable one is handed to the decoder with the gap marked.
- **The work a unit is charged** to the host's work budget, before the decoder does it (see "As built in step 3: TrueHD and MLP" in [the decoder design](../design/decoders.md)):
  - for each output sample of each substream decoded, the fixed work of a 24-bit sample for each matrix channel (19 units) and 48 units for the sample period of the substream (the noise generator, the history moves and the lossless check);
  - for each sample of a block, one unit for each tap of the filters of the coded channels, counted as 8 for a channel with a FIR filter and 4 for one with an IIR filter whatever their orders;
  - for each output sample, 3 units for each input of each primitive matrix (the matrix channels, and the two noise channels where the noise type is 0);
  - for a unit the walk did not store, which the decoder reads itself (the first unit of a stream, and every unit where the structure pass lends nothing), 16 units for each byte.
- The first unit of a stream is read by the decoder itself: the decoding session starts when its major sync has been read, so its walk had nothing to lend.
- A lossless check that differs is reported (`truehd.lossless-check-failed`, on the restart header whose check differs; on the unit where the tree keeps no detail for it, and on the stream for a unit past the limit of records); the verification counts every one that differs.

## Memory

- Units are kept as a 16-byte entry each, with a compact record for a unit with a major sync past the first 512; the figures of the series cost a few bytes more: about 50 bytes a unit on linux-x64 (`MemoryTests`).
- A TrueHD stream has 1,200 units a second at 48 kHz and up to 4,800 at 192 kHz, so a feature-length file has millions: past 2,097,152 entries (about 29 minutes at 48 kHz) a unit has no entry and costs about one byte, the share of the graphs' series (`truehd.units-limit`, info). A 2-hour stream projects to about 110 MB at 48 kHz and 140 MB at 192 kHz in all (`TrueHdUnitsPastTheLimitOfRecordsCostOnlyTheirFigures`). Measured with `encspect --csv` on files of a repeated 12-unit stream: 268 MB resident at 2.5 million units (35 minutes of 48 kHz, 270 MB with `--verify`) and 274 MB at 5.2 million units (72 minutes), against about 94 MB at 600,000 units before the limit and a projected 0.8 GB for a feature-length file.
- The decoder holds one unit's planes and the filter histories of three substreams, nothing per unit.

## Dolby Atmos

- A major sync with four substreams and bit 7 of the substream info set signals Dolby Atmos (`truehd.atmos-signaled`, info; the statistic `SignalsAtmos`). The wording is what the stream signals: the fourth substream is not described, FFmpeg does not decode it, and the analyzer reads the first three and checks the fourth against the directory only.
- The fourth substream's own parity byte and CRC-8 are neither checked nor counted: the description does not say that substream follows the rules of the others, so the tree shows its last two bytes as stored, without a verdict, and a failure there would mean nothing the analyzer can tell.

## Encoder detection

- None. The streams carry no encoder string, and the analyzer uses no pattern in their reserved fields as evidence of one: what the description observes of the encoders behind its test files is not a detection rule.

## Tags

- ID3v2 tags before the first unit and ID3v1 and APEv2 tags after the last are the host's. An ID3v2.4 SEEK frame that points between units ends the stream there and the bytes after the tag are reported as data (`truehd.data-after-tag`).

## Findings

- Stream: `truehd.no-units`, `truehd.data-skipped`, `truehd.data-after-tag`, `truehd.units-before-major-sync`, `truehd.units-after-damage`, `truehd.units-after-end-of-stream`, `truehd.stream-parameters-change`, `truehd.atmos-signaled`, `truehd.units-limit`.
- `truehd.unexpected-value` (info) also reports what the restart header says that FFmpeg does not check: channels coded past the last matrix channel, and a matrix channel that none of the substreams up to it codes (a matrix may still give it a value).
- Units: `truehd.unit-cut`, `truehd.unit-short`, `truehd.directory-invalid`, `truehd.parity-mismatch`, `truehd.major-sync-invalid`, `truehd.major-sync-crc-mismatch`, `truehd.unexpected-value`, `truehd.unsupported`.
- Substreams: `truehd.substream-parity-mismatch`, `truehd.substream-crc-mismatch`, `truehd.substream-end-invalid`, `truehd.substream-without-restart-header`, `truehd.restart-header-invalid`, `truehd.restart-header-crc-mismatch`, `truehd.parameters-invalid`, `truehd.block-invalid`, `truehd.block-size-check`, `truehd.parameters-change-within-unit`, `truehd.channel-assignment`.
- Decoding: `truehd.lossless-check-failed` and the host's `decode.*` findings.

## Not covered

- MP4 and Matroska: neither container routes the codec yet (`mp4.codec-not-analyzed`, `matroska.codec-not-analyzed`). The codec parser (`truehd` and `mlp`) is registered and reads a payload or packets handed to it; the routes come with the container steps.
- The fourth (Atmos) substream, and MLP streams whose two channel groups have different sampling rates (`truehd.unsupported`; the units are not read, decoded or counted).
- A negative output shift, and matrices with more than 5 channels together with noise type 0, which the description marks unsupported in FFmpeg (`truehd.unsupported`).
- Playback and any output other than the checksum comparison.
- Encoder identification (see above).

# FLAC

Native FLAC files (`.flac`, RFC 9639) and FLAC in Ogg (`.oga` and `.ogg`, through the Ogg parser's codec route).

## What is read

- The `fLaC` marker, then every metadata block by its 4-byte header (type, last-block flag, 24-bit length):
  - STREAMINFO: block sizes, frame sizes, sample rate, channels, bits per sample, total samples and the MD5 of the audio;
  - PADDING (a nonzero byte is a finding), APPLICATION (the registered ID), SEEKTABLE (every seek point, placeholders, order), CUESHEET (tracks and index points), PICTURE (type, MIME type, description, size and the length of the data, which is not read);
  - VORBIS_COMMENT, handed to the Vorbis comment tag parser by name (`ParseTag`);
  - reserved types (7 to 126) and the invalid type 127 (`flac.metadata-forbidden-type`).
- Every frame:
  - the header (sync code, blocking strategy, block size, sample rate, channel assignment, sample size and the UTF-8-coded frame or sample number) and its CRC-8;
  - every subframe: constant, verbatim, fixed predictor or linear predictor, with the order, the coefficient precision and shift, the wasted bits and the warm-up samples;
  - the residual: the method (4-bit or 5-bit Rice parameters), the partition order, every partition with its parameter, and escaped partitions with the width of their samples;
  - the padding to a byte boundary and the CRC-16.
- The samples are not decoded unless the front end asks for it (see "Decoding"); the structure pass reads every bit of the coded syntax, so a frame whose syntax is broken is found without it.
- The tree lists the first 4,096 frames in detail; the rest are listed in runs that add nothing. A file of any length costs a bounded amount of memory (see "Memory").

## Frames and resync

- A header counts as a frame only when its CRC-8 holds, its fields are legal and either the stream ends at the frame or a consistent header follows it. After bytes that are no frame, the next frame is found by a chain of such headers, never by the 14-bit sync code alone (it occurs in audio).
- A frame whose CRC-8 fails is still taken where its walk completes and a frame follows, since a flipped bit in the header's CRC-8 byte also fails the CRC-16 of a frame whose data is intact (`flac.header-crc-mismatch`).
- A frame whose CRC-16 fails is a frame with a finding (`flac.crc16-mismatch`); a frame cut by the end of the data is `flac.frame-cut`.
- A frame whose header is intact but whose subframes break the syntax (`flac.subframe-invalid`) is unreadable: it ends at the next chained header and is not decoded.
- Bytes between frames are shown as skipped (`flac.data-skipped`). The search for the next frame reads at most 64 MiB plus four times the file's length in all; past that the rest is skipped (`flac.resync-limit`).
- A frame may not be longer than 16 MiB; a longer one is a fault of its syntax.
- The frame or sample number must follow without a gap: a jump is `flac.sample-number-discontinuity`. A stream that does not start at sample 0 is `flac.first-sample-not-zero`.
- Frames whose block size, sample rate, channels or bit depth differ from STREAMINFO's are `flac.frame-differs-from-streaminfo`; a block size over STREAMINFO's maximum is `flac.block-size-out-of-range` and a frame larger than its maximum frame size `flac.frame-size-out-of-range`.

## Reported

- the sample rate, channels and layout, bits per sample, the samples (from the frames; STREAMINFO's count is compared, `flac.total-samples-mismatch`, and 0 means unknown, `flac.total-samples-unknown`), the duration and the average bitrate, which counts the bytes of the frames;
- the bitrate per frame and per second as series, with the smallest and largest and a distribution; the bitrate mode is variable where the frame bitrates differ (`IsVbr`);
- statistics: the blocking strategy, the block sizes, the share of each stereo assignment, the subframe types with the predictor orders and coefficient precisions, the wasted bits, the partition orders, the Rice parameter width, the escaped partitions, the checksums that held and failed, and the MD5.

## Channel layout

- One to eight channels in FLAC's order (RFC 9639, section 9.1): mono is front center; two channels are front left and right; three add front center; four are front left, front right, back left, back right; five add front center before the back pair; six add the low-frequency channel before the back pair; seven and eight follow the layouts of the specification's table.
- The channel mode is Mono, Stereo, Joint stereo or "Stereo, joint in some frames" from the share of left/side, side/right and mid/side frames.
- Left/side, side/right and mid/side are shown as such; the side channel of a 32-bit stream has 33 bits.

## Encoder detection

- libFLAC: the Vorbis comment vendor string `reference libFLAC <version> <date>` is the encoder, with high confidence. A vendor string of that form with a version the analyzer cannot parse is "libFLAC" alone.
- FFmpeg's native encoder: an `ENCODER` comment of the form `Lavc<version> flac` is the encoder, with medium confidence, since the comment is a tag any tool may write.
- An ID3v2 or APEv2 claim of the encoder or writer weighs in as in the other formats (`flac.writer-claim-differs`, `flac.writer-other-format`, `flac.encoder-other-format`).
- The compression level is not detected: the frames do not say which level wrote them. The statistics (block size, predictor orders, partition orders, the share of stereo assignments) show what the encoder chose.

## Ogg FLAC

- The first packet is the mapping header: `0x7F`, `FLAC`, the major and minor version, the 16-bit count of header packets, `fLaC` and the STREAMINFO block. Then one metadata block per header packet, then one frame per packet.
- The tree is the same as a native file's. Findings specific to the mapping: `flac.ogg-version`, `flac.ogg-header-count`, `flac.ogg-header-invalid`, `flac.ogg-packet-invalid`, `flac.ogg-packet-trailing` and `flac.ogg-granule-mismatch` (the page's granule position against the samples counted).
- A packet that spans pages is shown with its byte ranges.
- A frame in an Ogg stream is decoded from the packet's bytes.

## Decoding

- The decoder (`Encspect.Formats.Flac.Decoding`, ID `flac`) runs when the front end asks for it: `--verify` on the command line, "Verify Audio" in the detail window. The tree is the same either way.
- It reads no coded syntax of its own. The structure pass stores each frame's warm-up samples, residuals and the description of every subframe in buffers the host lends, and the decoder adds what the syntax does not give: the predictors, the wasted bits and the undoing of the channel assignment, then the MD5 of the samples.
- The MD5 is over the interleaved samples, signed, little-endian, in as many whole bytes as the bit depth needs (RFC 9639, section 8.2). An MD5 of 0 means the encoder did not store one.
- Three checksums are declared and tallied:

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| frame header CRC-8 | the header | never |
| frame CRC-16 | the coded data | no gap, the frame count matches the samples STREAMINFO states, no cut frame and no unreadable frame |
| STREAMINFO MD5 | the decoded audio | the decoder decoded every frame and the sample count matches |

- A stream is verified when every stored checksum was compared, none failed, and at least one compared checksum covers all the audio. Frames whose every CRC holds do not verify a stream that is missing frames, has them out of order or has a frame it could not read: with an MD5 of 0, such a stream reads "unknown" (`encspect --learn verification`).
- Findings from the decoder: `flac.md5-mismatch` (the MD5 differs), `flac.md5-not-stored` (an MD5 of 0, with the CRCs as the only check) and the host's `decode.*` findings (budget, stop, failure).
- A stream whose frames differ from STREAMINFO in channels or bit depth is not decoded (`NotPossible`); so is a STREAMINFO with fewer than 4 bits per sample.
- The 33-bit side channel and 32-bit data are decoded in 64-bit planes; the other streams use 32-bit planes.

## Memory

- The frames are kept as a 56-byte entry each and expanded into nodes when the tree is opened; the frame details (subframes, partitions) are kept in compact records. A file of a million frames costs tens of megabytes, not a node tree.
- The metadata is capped per block (1,024 blocks listed, 1 MiB of a block's body read) and the comment, picture and cuesheet text per field.
- The decoder holds one frame's planes and the MD5 state, nothing per frame.

## Tags

- A Vorbis comment block is read by the Vorbis comment parser. `flac.comment-not-read` shows when none is available to the analyzer.
- ID3v2 tags before the marker and ID3v1 and APEv2 tags after the last frame are the host's. An ID3v2.4 SEEK frame that points between frames ends the stream there and the bytes after the tag are reported as data (`flac.data-after-tag`).
- A tag candidate inside the chain of frames belongs to the stream.

## Findings

- Stream: `flac.no-marker`, `flac.no-streaminfo`, `flac.no-frames`, `flac.total-samples-mismatch`, `flac.total-samples-unknown`, `flac.first-sample-not-zero`, `flac.blocking-strategy-changes`, `flac.sample-number-discontinuity`, `flac.data-skipped`, `flac.resync-limit`, `flac.data-after-tag`, `flac.payload-not-analyzed`.
- Metadata: `flac.streaminfo-invalid`, `flac.streaminfo-length`, `flac.streaminfo-misplaced`, `flac.metadata-forbidden-type`, `flac.metadata-no-last-block`, `flac.metadata-truncated`, `flac.metadata-block-repeated`, `flac.metadata-blocks-not-listed`, `flac.padding-not-zero`, `flac.seek-table-length`, `flac.seek-table-unordered`, `flac.seek-table-placeholder`, `flac.cuesheet-invalid`, `flac.cuesheet-reserved`, `flac.picture-invalid`, `flac.comment-not-read`, `flac.comment-trailing-bytes`.
- Frames: `flac.header-crc-mismatch`, `flac.crc16-mismatch`, `flac.frame-cut`, `flac.subframe-invalid`, `flac.padding-bits-set`, `flac.frame-differs-from-streaminfo`, `flac.block-size-out-of-range`, `flac.frame-size-out-of-range`.
- Decoding: `flac.md5-mismatch`, `flac.md5-not-stored`, `decode.frame-too-large` (a guard: a frame the specification allows cannot reach it).
- Encoder: `flac.encoder-other-format`, `flac.writer-claim-differs`, `flac.writer-other-format`.
- Ogg: `flac.ogg-version`, `flac.ogg-header-count`, `flac.ogg-header-invalid`, `flac.ogg-packet-invalid`, `flac.ogg-packet-trailing`, `flac.ogg-granule-mismatch`.

## Not covered

- Playback and any output other than the checksum comparison (the PCM sink is for tests and a later export).
- FLAC in MP4 and Matroska: the container names the codec and the payload is not analyzed (`flac.payload-not-analyzed`, an info finding); only Ogg hands over packets.
- The contents of PICTURE data and the audio of an APPLICATION block.
- Subset conformance (the limits of the FLAC subset) is not judged.

# WavPack

WavPack 4 and 5 files: `.wv` (lossless, hybrid and DSD) and the `.wvc` correction files of hybrid mode.

## What is read

- Every block, by its 32-byte header:
  - the signature, the size, the version (0x402 to 0x410), the 40-bit block index and total samples, the sample count, the flags and the CRC;
  - every flag as a typed field with a description: bytes stored, mono, hybrid, joint stereo, cross-channel decorrelation, noise shaping, float data, integer shift, the sample rate, DSD and the rest.
- The ID byte of a sub-block is shown as its function (the low six bits, the optional bit 0x20 among them), whether it is optional, whether its size is odd and whether it is large.
- Every metadata sub-block of a block, named by its ID. The structured ones are decoded:
  - decorrelation terms, weights (per pass, and the samples pass by pass), entropy variables and the hybrid profile; the two channels of a stereo block are named left and right, or side and mid where the block uses joint stereo, since the encoder codes the first as the side (left less right) and the second as the mid (pack.c);
  - noise shaping weights, which stay left and right even with joint stereo since the noise is shaped before the conversion, float and extra-bits information;
  - the channel layout (channel info, channel identities, the channel mask), the DSD sub-block's mode;
  - the configuration block (the encoder's mode flags, among them whether the hybrid bitrate was given in kbps, whether the stream is the older version 0x407 that the legacy library function writes, and the level of extra processing) and the new configuration block;
  - the MD5 checksum, the block checksum and the RIFF, AIFF or CAF wrapper the encoder kept (header and trailer, with their chunks).
- The block checksum is recomputed and compared; a missing or wrong one is a finding.
- Blocks of one multichannel set (the initial and final block flags) and their block indexes, which must follow without gaps or overlaps.
- Runs of bytes that are no block are shown as skipped. A header is plausible under libwavpack's `read_next_header` checks (signature, even size from 24 bytes to under 1 MiB, major version 4, fewer than 0x30000 samples), but the reader differs from libwavpack in four ways:
  - after bytes that are no block, a header counts only if its block ends at the end of the data or at another plausible header (the chain requirement), since the four signature bytes can occur in the audio; libwavpack takes the first plausible header;
  - the minor version is not a condition: a block of a version libwavpack refuses is still a block, with `wavpack.version-unsupported`;
  - a block whose stated size does not end at a header or at the end of the data is taken to end at the next chained header inside it (`wavpack.block-size-mismatch`);
  - there is no limit of 1 MiB of skipped bytes, after which libwavpack gives up.
- Once 4,096 blocks are detailed, runs of further blocks that add nothing become one node each.
  - Past the detailed blocks an audio block keeps a 64-byte entry of its own; only blocks that are a 32-byte header and count no sample join runs of one record each.
  - The sub-blocks listed and the bytes kept are capped per block and across all the detailed blocks (65,536 sub-blocks and 16 MiB).
  - The memory tests hold a block to about 115 bytes, 4,096 blocks of 1,024 sub-blocks to 3 MB, 700 blocks of 64 KiB of optional sub-blocks to 18 MB, and a flood of empty blocks to one record; the complexity test holds repeated headers that end in no header to linear time.
- What the detailed blocks list and keep is capped per block (1,024 sub-blocks, 64 KiB of data) and across all of them: 65,536 listed sub-blocks and 16 MiB of kept data in all. The sub-blocks past a cap are counted as "Further Sub-blocks", never listed without their data: a sub-block that does not fit what is left in its block or in all is counted with the rest of that block, and once the 16 MiB are spent, later blocks show only their header and the count. A crafted file therefore costs a bounded amount of memory, whatever the number of its blocks and sub-blocks.

## Reported

- **Stereo Coding**, a tally and a category series of the coding of each stereo block (left/right or mid/side) in the sets of the graphs, in place of "Mid/Side Blocks"; DSD streams have none.
- the sample rate (a custom rate from its sub-block, ignored where it is 0 as libwavpack ignores it; for DSD the native bit rate, held to 2,147,483,647 Hz), channels and layout, bits per sample, the total samples and the duration (the samples are those of the first block of each set; Lost Samples counts a set once for any block in it that is cut, malformed or refused, and is 0 for correction data and for a stream version libwavpack does not decode);
- "WavPack lossless", "WavPack hybrid", "WavPack lossy" or "WavPack correction data" as the codec;
- the hybrid target bitrate in bits per sample, from the profile's accumulator ("or less" where the accumulator is 0, since the encoder stores 0 for any target below 568/256 bits per sample, about 2.22; for a false stereo block, whose accumulator is worked out from twice the target, below 540/256, about 2.11), and the average bitrate, which counts the bytes of the audio blocks less the wrapper sub-blocks (the source file's header and trailer) they hold;
- the bitrate per block and per second, as series, with the smallest and largest block, a distribution and the bitrate mode: variable or constant for two sets or more, unknown for a single set, which shows no variation (`IsVbr` is false then);
- the bits per sample as the sample size less the left shift; a shift that leaves no bits is a finding and the bit depth is then not reported.

## Wasted Bits

- **What it counts:** the number of audio blocks with each count of Zero Bits (the INT32_INFO sub-block; 0 where a block has none), and **Changes**: the blocks whose count differs from the count of the block at the same place in the previous block set. It is FLAC's **Wasted Bits** statistic under the same name, with blocks in place of subframes. The count of a block holds for all its channels (the smallest of theirs), so a stereo file has one count where FLAC has two.
- **When it shows:** only in a stream where some block has Zero Bits. DSD blocks and correction files (blocks without a main bitstream) are not counted.
- **Left Shift** is a different field of the block header and is not counted; it stayed 0 in the lossyWAV fixtures.
- **In Matroska** the codec route counts the Zero Bits of each packet's blocks the same way and makes the same claim (`matroska/external-mkvmerge-wavpack-lossywav.mka`). It also requires a main bitstream in the block, as the file reader does.
- **Silent blocks:** a block whose header maximum magnitude (flags bits 18 to 22) is 0 and that has no Zero Bits is taken as silent. By the format document such a block could also hold samples of one bit; wavpack 5.6.0 and FFmpeg's encoder write it for blocks of zeros (a 4-byte bitstream, and a block of 52 bytes), and the other blocks of the same files read 14. They are listed as **Silent**, left out of the other entries, their shares and the changes, and leave the previous count in place, as FLAC's constant subframes of 0 do. Float blocks are not judged this way. Fixtures: `external-silence-16-in-24-merge-blocks.wv`, `external-ffmpeg-silence-16-in-24.wv` (FFmpeg's encoder with `-frame_size 512`) and `matroska/external-mkvmerge-wavpack-silence.mka`, none claimed.
- **The claim from behavior** (lossyWAV, low confidence) follows the same rule as FLAC's ([flac.md](flac.md#encoder-detection)): the distances between the changes must have the lossyWAV block as their greatest common divisor, which blocks of whole multiples of 512 samples (`--merge-blocks`) can show. WavPack's default blocks (11,025 samples at 44.1 kHz) put the changes at multiples of 11,025, so their counts are shown and make no claim. The fixtures that carry the marker get the figures as a second piece of evidence: `external-lossywav-merge-blocks.wv` has 36 changes, all on the 512 grid; `external-lossywav-synthetic-merge-blocks.wv`, whose tones keep every bit, has 30 changes and 54.1% of its blocks above the smallest, so the rule's share (more than half) passes narrowly.

## Hybrid mode and correction files

- **Names as a second signal (rule, 2026-11-09):** WavPack makes no claim from a file name today: hybrid mode is read from the block flags. Where a claim of this shape is made (OptimFROG's lossy part of a DualStream, [optimfrog.md](optimfrog.md)), the extension (`.wv`, `.wvc`) may raise a claim the bytes make from low to medium confidence, never makes one alone and never decides detection; the 0.4 pairing finds `x.wvc` beside `x.wv` by base name and extension.

- A hybrid `.wv` is lossy by itself. A `.wvc` file holds the correction that makes it lossless: its blocks are read the same way and the stream is reported as correction data.
- A `.wvc` opened alone is a finding (`wavpack.correction-file`), not an error.
- The extra bitstream (wvx) that 32-bit integer and float data need can sit in the `.wv` or in the `.wvc` block; a block that holds only that is not a block without a bitstream.
- The analyzer reads the file it is given. It does not look for the other file of a pair.
- `IsCorrectionFileCreated` in the statistics is true for a correction file, and for a stream whose configuration block says the encoder wrote one (`CreatesCorrectionFile`); it does not say that the file is next to this one.

## Channel layout

- From the channel mask of the channel info sub-block (WAVEFORMATEXTENSIBLE speaker bits).
- Without a mask: mono and stereo from the flags, and discrete channels otherwise. A channel info of a count of 0 and no mask is accepted by libwavpack and counts as none.
- libwavpack settles the channels when it reaches the first audio block that passes its block check (its open loop skips a block whose checksum fails, for instance): from the channel info of that block (or of a metadata block before it that passes) that gives channels, else from the block's flags (mono or stereo, mask 4 or 3). It checks that channel info fully (the count against the streams, the speakers of the mask against the count).
- A channel info after that point only has to be 1 to 7 bytes long and never changes the channels. One that fails the other checks is therefore not reported as refused; one that gives other channels or another mask than the settled ones is `wavpack.channel-info-ignored`.
- The channel identities name the channels after the ones the mask names, as libwavpack lists them (`WavpackGetChannelIdentities`): with a mask of two speakers, the first identity is channel 3.
- A set of blocks has at most as many blocks as libwavpack reads into one: the streams the long form of the channel info states (up to 4,096), else 8. A set that has more without a final block is ended there (`wavpack.block-set-broken`).
- A channel count in the channel info that differs from the channels of the first set of blocks is a finding (`wavpack.channel-count-mismatch`).

## Encoder detection

- "WavPack 5.0 or later" where the blocks hold a new configuration block or a block checksum, which libwavpack has written since 5.0, as libwavpack tells encoder versions 5 and 4. The confidence is medium: the library writes no encoder name. FFmpeg 6.1.1's encoder writes neither.
- "WavPack 4 stream, no WavPack 5 features" for the others, with low confidence: libwavpack reports encoder version 4 for them, but other encoders write such streams too (FFmpeg's writes version 0x410 with no new configuration block and no checksums). The evidence says so.
- An ID3v2 TSSE frame weighs in as a claim of the encoder role. A claim that names WavPack is evidence; one that names FFmpeg is evidence for a WavPack 4 stream, which FFmpeg writes; a claim that names anything else is a finding (`wavpack.encoder-claim-differs`).
- An APEv2 Encoder item (the key in any case, which FFmpeg writes as `encoder` with its Lavf version) and ffmpeg's muxer version name the writer, with low confidence, apart from the blocks' encoder; two that name different software are `wavpack.writer-claim-differs`, and software known to write another format (LAME on a WavPack stream) is `wavpack.writer-other-format`.
- lossyWAV (a **processor**, not an encoder or a writer): the RIFF header the wrapper keeps lists the chunks of the source WAVE file up to the data. A `fact` chunk there whose body starts with `lossyWAV ` is shown as a "Fact Chunk" with its ID, size and "Text" node (version, date and time, settings), and the stream's Processor is "lossyWAV 1.4.2" with high confidence and the settings as evidence. The shared reader is `FactText`. A text that starts with `lossyWAV ` but has not the form is `wavpack.wrapper-fact-text-unrecognized` (info) and makes no claim. The trailer is not searched: a chunk there is after the data. See the `lossywav` article. Only the first such text is read; a second one in the wrapper is ignored.
- Configuration flags are shown as the command line options that set them (`-f`, `-h`, `-hh`, `-x`, `-c`, `-m`).
- Observed in Encspect's test files, and not used by the analyzer:
  - the first audio block of each of the 53 `.wv` files from wavpack 5.6.0 holds a config block, a new config block and a block checksum;
  - the one `.wv` file from FFmpeg 6.1.1's encoder holds none of the three;
  - the 8 `.wvc` correction files hold a block checksum and neither of the others.
- That is one file of FFmpeg's encoder and none of a libwavpack older than 5.0, so the absence of a config block is not a rule: the analyzer keeps the low-confidence "WavPack 4 stream" and says in its evidence that another encoder may have written it.

## Decoding

- The decoder (`Encspect.Formats.WavPack.Decoding`, ID `wavpack`) runs when the front end asks for it: `--verify` on the command line, "Decode Audio" in the detail window (which also measures the audio). The tree is the same either way.
- It decodes lossless and hybrid streams (integers of 1 to 32 bits, floats, up to the channels a stream may have) and DSD (stored, fast and high modes). The syntax of the sub-blocks is read by the parser's code (`BlockContents`); the decoder adds the entropy decoding, the decorrelation passes, joint stereo, the shift and the sent, zero, one and duplicate bits, the restoring of floats, and the checksums.
- A hybrid file is decoded without its `.wvc` to the lossy audio the main file holds; the analyzer does not look for the correction file.
- Four checksums are declared and tallied:

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| block checksum | the coded data | the rule of the next row holds and every audio block has one (a block that says it has one and has none counts as failed) |
| block header CRC | the decoded audio | no gap in the block indexes, no block the parser could not read, no broken set and the samples of the blocks match the total the stream states |
| extended CRC | the decoded audio of floats and of integers above 24 bits | never (it covers the extra bits of the blocks that have them only) |
| MD5 | the decoded audio | always; it is compared only when the decoder decoded every block, nothing is missing, the stream is lossless and no set is over the ceiling |

- A stream is verified when every stored checksum was compared, none failed, and at least one compared checksum covers all the audio. A hybrid file without its `.wvc` is verified as its lossy stream by the CRCs of its blocks (`IsLossy`); its MD5 is of the lossless audio and is not compared (`wavpack.md5-needs-correction-file`).
- The MD5 is over the samples in the layout of the source (the new config block's qmode): little-endian by default, big-endian for AIFF and CAF sources, signed bytes, unsigned words, reordered channels, and for DSD the bytes in blocks and the bit order of the source. The last 16-byte MD5 sub-block of either kind is taken, as libwavpack does when it opens a file with `OPEN_ALT_TYPES`. The layouts are libwavpack's (`pack_utils.c`, `wvunpack.c`).
- Findings from the decoder: `wavpack.audio-crc-mismatch` (the CRC of the decoded audio of a block differs from the stored one), `wavpack.md5-mismatch`, `wavpack.md5-needs-correction-file`, `wavpack.md5-set-too-large`, and the host's `decode.*` findings. A block the parser could not read, or that libwavpack's decoder refuses, is handed as unreadable and counts as not decoded.
- Decoding starts at the first audio block that verifies, as libwavpack opens a file. A stream whose first configured block libwavpack refuses (a decorrelation term of 0, a sub-block of the wrong size, a DSD high block that is too short) cannot be decoded: its decoding is `NotPossible`.
- A set of blocks is decoded into planes for all its channels; a set over 2^24 samples (all channels together) is decoded block by block with only the CRCs compared (`wavpack.md5-set-too-large`).
- A block is muted, and fails its CRC, when it has fewer samples than the first block of its set states (`unpack.c` 89, `unpack_dsd.c` 74; the block's own index plays no part, as `open_utils.c` 357 takes it from each block), or when its bitstream runs out before its samples do, or when a decoded sample passes libwavpack's mute limit (libwavpack compares the CRC the block was left with, which in its stereo path runs to the end of the 4,096-sample chunk where the mute hit, so it reports an error only when the mute falls before the block's last chunk; the analyzer deliberately counts every muted block's CRC as failed). The decoder mutes the whole block (zeros, 0x55 for DSD). libwavpack mutes the first case the same way, but never checks for the end of a bitstream: its reader goes on from the start of the bitstream again (`open_utils.c` 842-846) and mutes only on its end-of-data code or its mute limit, so the audio of a block cut short differs from libwavpack's, and both fail the CRC.
- A block whose CRC fails is not zeroed; the samples are kept as decoded.
- The cancellation token is checked in the long loops. Every block is charged to the host's work budget before it is decoded (see below), the blocks of a set that never completes (no final block, channels that do not add up) as any other, so the budget bounds them and `decode.limit` ends the decoding of a crafted file of them. The decoder holds one block's state and the set's planes, nothing per block.
- **The work a block is charged:**
  - for each sample of each channel the block outputs, a muted block's too: the fixed work of the sample for its width (`DecodeWork.ForSample`: 13, 16, 19 or 22 units for 1 to 4 bytes, 13 for a byte of DSD) and, for integers and floats, 24 more for the entropy decoder's word, the mute check, the CRC and the copy into the set's planes, and 16 more for restoring a float;
  - 16 for each byte of the bitstream sub-blocks it reads (the main one, the extra one, the DSD data);
  - 11 for each decorrelation pass, for each sample of each channel the passes run on;
  - DSD: 32 more for each byte of each channel in the fast mode and 128 in the high mode, and the setup of the block's tables, 1,792 units for each of the 2^history bits bins in the fast mode (57,344 at 5 bits) and 4,096 in the high mode.
  - The weights, and how they were measured, are in "As built: the work budget" of [the decoder design](../design/decoders.md).
- The exponent loop of libwavpack's `float_values` (`unpack_floats.c` 70-71 and 118-119) is computed in closed form, without iterating: for a value that is 0 or whose set bits all lie above bit 23, which a crafted file reaches with a few bytes, the loop counts the exponent down to 0 one step at a time. The result, the shift count and the CRC over the floats are the same.

## Matroska

- The `wavpack` codec parser reads the packets of an `A_WAVPACK4` track: a packet is the sample count, and for each block its flags, CRC and size (the size is left out when the block holds the initial and the final flag), then the block's data without the 32-byte header (the Matroska codec mappings, `draft-ietf-cellar-codec` sections 3.4.42 and 4.1, which also make the CodecPrivate the 16-bit stream version of the block header). It rebuilds the headers (the version from the two bytes of CodecPrivate; block index and total samples 0). Where the CodecPrivate has no version it assumes 0x410, the newest of the versions WavPack's "4 & 5 Binary File / Block Format" lists as valid for decode (0x402 to 0x410); neither source states what a reader assumes, so this is the analyzer's own choice and analyzes and decodes the blocks as a file's.
- The packet nodes list the first 256 packets; later decode findings have no node of their own to point to.
- Findings: `wavpack.matroska-packet-invalid`, `wavpack.matroska-packet-cut` (the file ends inside a packet), `wavpack.matroska-block-invalid`, `wavpack.payload-not-analyzed` (a container handed the whole payload). Matroska routes `A_WAVPACK4` tracks to the parser (see [the Matroska format](matroska.md)): the CodecPrivate is the stream version, and a packet that is cut, or holds a block that cannot be read, is counted in the stream's lost samples with the samples it declares. A track whose frames are zlib-compressed or header-stripped is not routed (`matroska.track-encoding-not-read`). The stream gets the channel mode a `.wv` file gets (mono, stereo, joint stereo with mid/side in all or some blocks, multichannel), counted from the stereo blocks of the packets, and its Encoder weighs the container's claims (a Lavc tag, a WritingApp) as a file's tags are, with `wavpack.encoder-claim-differs` for one that disagrees with the blocks.

## Tags

- APEv2 and ID3v1 tags after the last block are the host's, as are ID3v2 tags in front. An ID3v2.4 SEEK frame that points between blocks ends the stream there, whether or not the data starts with a block, and the bytes after the tag are reported as data (`wavpack.data-after-tag` where the stream declares no end).
- A tag candidate inside the chain of blocks belongs to the stream.

## Findings

- A code that stands for several problems is reported once for each problem (`wavpack.subblock-size` once for each kind of sub-block, for instance), each with its count, so the message of a finding never names a problem of another.
- Blocks: `wavpack.version-unsupported`, `wavpack.flags-invalid` (the mono and false stereo flags both set, a left shift not less than the sample size), `wavpack.reserved-flag`, `wavpack.block-size-mismatch`, `wavpack.block-truncated`, `wavpack.block-trailing-byte`, `wavpack.block-checksum-mismatch`, `wavpack.block-checksum-missing`, `wavpack.data-skipped`.
- Sub-blocks: `wavpack.subblock-overrun`, `wavpack.subblock-size` (including decorrelation weights or samples before any decorrelation terms, and a new config block that reorders more channels than its layout has), `wavpack.subblock-unknown`, `wavpack.optional-subblock-unknown`, `wavpack.decorr-terms-invalid`, `wavpack.channel-info-invalid`, `wavpack.channel-info-ignored`, `wavpack.dsd-invalid` (including a stored mode whose audio is not exactly the bytes the block counts, a fast mode that ends right after the history bits, and a high mode with fewer than 13 (mono) or 20 (stereo) bytes after the mode or a rate step other than 20), `wavpack.dsd-flag-mismatch`, `wavpack.sample-rate-missing`, `wavpack.sample-rate-zero`, `wavpack.sample-rate-implausible` (a DSD bit rate over 2,147,483,647 Hz), `wavpack.audio-without-bitstream`.
- Stream: `wavpack.no-blocks`, `wavpack.no-audio`, `wavpack.first-block-index`, `wavpack.block-index-gap`, `wavpack.block-index-overlap`, `wavpack.block-set-broken`, `wavpack.total-samples-mismatch`, `wavpack.total-samples-unknown`, `wavpack.format-changes`, `wavpack.mixed-streams`, `wavpack.channel-count-mismatch`, `wavpack.correction-file`, `wavpack.encoder-claim-differs`, `wavpack.writer-claim-differs`, `wavpack.writer-other-format`, `wavpack.wrapper-fact-text-unrecognized`.
- Decoding: `wavpack.audio-crc-mismatch`, `wavpack.md5-mismatch`, `wavpack.md5-needs-correction-file`, `wavpack.md5-set-too-large`.
- Matroska: `wavpack.matroska-packet-invalid`, `wavpack.matroska-packet-cut`, `wavpack.matroska-block-invalid`, `wavpack.payload-not-analyzed`.

## Not covered

- Playback and any output other than the checksum comparison (the PCM sink feeds the measurements and tests, not an export). DSD is decoded to its 1-bit samples for the CRC and the MD5, not to PCM.
- Zeroing or concealing a block that fails its CRC.
- WavPack hybrid correction data in Matroska (BlockAdditions, BlockAddIDType 1) is not read; the stream is reported from its main blocks.
- WavPack in Matroska: Verified Block Checksums is not reported, since the block checksum covers the block's header, which Matroska does not keep; the other figures of a file (Stereo Coding, Wasted Bits and the low-confidence lossyWAV claim, counted from each packet's blocks at the position of the samples before it, the bitrate series and statistics, the block set figures, Bits Per Sample, IsLossless, Decorrelation Passes, the compression mode, Channel Layout, the encoder) are reported from the flags and sub-blocks the packets keep.
- WavPack in Matroska: the lost samples of a packet are the samples its first four bytes declare, so a packet too short to hold them is not counted, and a packet without a block is not counted in the samples either.
- The legacy format of WavPack 1 to 3 (`.wv` files without the "wvpk" blocks; planned in 0.4, a separate item).
- Finding the `.wvc` that belongs to a `.wv` (planned in 0.4, needs host access to a companion file).

## Articles

- **Articles:** `wavpack-blocks`, `wavpack-sub-blocks`, `wavpack-hybrid-mode`, `wavpack-decorrelation`, `wavpack-dsd`, `wavpack-float-and-large-integers`, `wavpack-wrapper`.
- **Shared with other formats:** `mid-side-stereo`, `crc`, `bitrate-modes`, `encoder-detection`, `lossywav`.

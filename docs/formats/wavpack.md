# WavPack

WavPack 4 and 5 files: `.wv` (lossless, hybrid and DSD) and the `.wvc` correction files of hybrid mode; and, from the same parser, the `.wv` files of WavPack 1 to 3 (see [WavPack 1 to 3](#wavpack-1-to-3)).

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
- the sample rate (a custom rate from its sub-block, ignored where it is 0 as libwavpack ignores it; for DSD the native bit rate, held to 2,147,483,647 Hz), channels and layout, bits per sample, the total samples and the duration (the samples are those of the first block of each set; Lost Samples counts a set once for any block in it that is cut, malformed or refused and for a set that begins without the initial block flag (libwavpack reads on to the next set that has it), and is 0 for correction data and for a stream version libwavpack does not decode);
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

- **Names as a second signal (rule, 2026-11-09):** WavPack makes no claim from a file name today: hybrid mode is read from the block flags. Where a claim of this shape is made (OptimFROG's lossy part of a DualStream, [optimfrog.md](optimfrog.md)), the extension (`.wv`, `.wvc`) may raise a claim the bytes make from low to medium confidence, never makes one alone and never decides detection; the pairing finds `x.wvc` beside `x.wv` by base name and extension (below).

- A hybrid `.wv` is lossy by itself. A `.wvc` file holds the correction that makes it lossless: its blocks are read the same way and the stream is reported as correction data.
- A `.wvc` opened alone is a finding (`wavpack.correction-file`), not an error.
- The extra bitstream (wvx) that 32-bit integer and float data need can sit in the `.wv` or in the `.wvc` block; a block that holds only that is not a block without a bitstream.
- A `.wvc` opened alone says in its finding to analyze the `.wv` beside it, and its stream is a correction file's (`IsCorrectionFile`: a report of its own that counts in no duration, samples or bitrate of the main file; its own figures are those of the correction data alone).
- **The pair** ([correction-files.md](../design/correction-files.md)):
  - a stream with a hybrid block asks the host for its correction file, which the host looks for beside the input by the name rule (`x.wv` to `x.wvc`, in the case of the input's extension; a file under another extension is paired by its stem); a stream, standard input or a caller with no folder reports the pair as `unchecked` (`correction.unchecked`);
  - a found file is read with the walk of a file of its own, so its blocks, block checksums and sub-blocks are the ones it gets alone, in a report of its own inside the main file's (`Report.Companions`, root "Correction File"); only `wavpack.correction-file`, which says it was opened alone, is left out. A file that holds a main bitstream or no audio (a lossless `.wv`, an empty file) is `NotCorrectionData` and gets no stream of its own (a WavPack 1 to 3 file is read as the legacy format it is, with its tree as it has alone, but gets no stream of its own, no decoding and no correction file of its own asked for; the host also answers `unchecked` to any parser that asks for a correction file from the context over a companion);
  - the main file's hybrid blocks with audio are matched to the correction file's blocks in step, as libwavpack's `read_wvc_block` and `match_wvc_header` do (`open_utils.c`): the same block index and sample count and the same flags is a match; a correction block before the main block's index, or one whose initial and final flags place it earlier in a set, is skipped and counted as one no main block matches (`blocksUnmatched`); a main block with none is `blocksMissing`;
  - `streamDiffers`: the first blocks differ in stream version, total samples, first block index or the flags that state the format (sample size, mono, float, 32-bit integer, false stereo, shift, rate, DSD); the blocks are not matched then, since all of them would differ for that one cause;
  - `checksumsFail`: a correction block whose block checksum fails or is missing, or whose sub-blocks do not end with it, which libwavpack does not use (`WavpackVerifySingleBlock`); `cut`: a correction block cut by the end of the file or longer than its place, and bytes between and after the blocks that hold no block;
  - a pair with no problem is `consistent`; the status of a pair is the structure's: the audio of a pair whose correction blocks match but belong to another encode (the same index, samples and flags) is for the decoder to compare. A correction file found beside a file whose configuration block says the encoder wrote none is checked as any other; with none found that file is `notWritten` and raises nothing, and a written one that is not there is `missing`;
  - the verification gains a tally "correction block checksum" (coded data) for the correction file's block checksums, where the correction file is used: it holds correction data of the same stream and a block of it matches a block of the file. A correction file that states another stream, or none of whose blocks match, is not the stream's, and its damage does not change the verdict;
  - **Decoding a pair** (`--verify`, "Decode Audio"): the decoder applies the correction as libwavpack's hybrid lossless path (`unpack.c`) does. For each hybrid block the host's feed takes the matching correction block (the same matching as above; one whose checksum fails or that is cut is not used) and the decoder reads its correction and extra bitstreams and its shaping weights, decodes each sample with its correction (with the noise shaping of the lossy part) and compares the CRC of the correction block's header, which is the CRC of the lossless audio (libwavpack copies it over the main block's), the CRC of the extra bits where the correction block holds them, and the MD5. The verification then reads "with correction file" instead of "lossy stream", and the MD5 is compared where every hybrid block has its correction block;
  - a block of the pair without a correction block (the correction file lacks it, or its checksum fails) is decoded as the lossy part, as libwavpack decodes it, and counted as libwavpack counts it, as an error (`wavpack.correction-block-missing`, one failed block header CRC each; the count equals `WavpackGetNumErrors` less the blocks whose CRC fails), so the stream is not verified; the MD5 is then not compared (`wavpack.md5-needs-correction-file` says how many blocks were decoded without one). A correction file the pairing says is another stream's is not used at all, and the stream is decoded as its lossy part. The verification says "with correction file" only where some block took its correction, else "lossy stream". A pair whose blocks match and whose audio does not (a correction file of another encode with the same flags) shows in the CRCs (`wavpack.audio-crc-mismatch`);
  - the decoded audio of a pair equals libwavpack's pass over it and `wvunpack --raw` with the `.wvc` beside the file, except for a block whose CRC fails, which libwavpack mutes and the analyzer keeps (`crosscheck_wavpack_fixtures.py`, `PAIR_AUDIO_DIFFERS`).
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
  - the 8 `.wvc` correction files hold a block checksum and neither of the others;
  - the 7 `.wv` files of the DOS build of 4.31 (stream version 0x404, `external-wv431-*`) hold a config block and neither a new config block nor a block checksum, and the one `.wvc` of it holds none of the three.
  - in them, extra processing sets its flag without a level byte, so the Extra Processing Level reads "not stored" (libwavpack reads the level as 0, unknown); and the high mode is reported as high, as the file's flag and 4.31 name it, where libwavpack's `WavpackGetMode` reports a stream older than 0x405 as very high, the same algorithm under its later name (`common_utils.c`), which `crosscheck_wavpack_fixtures.py` maps.
- That is one file of FFmpeg's encoder and the files of one libwavpack older than 5.0, so the absence of a config block is not a rule: the analyzer keeps the low-confidence "WavPack 4 stream" and says in its evidence that another encoder may have written it.

## Decoding

- The decoder (`Encspect.Formats.WavPack.Decoding`, ID `wavpack`) runs when the front end asks for it: `--verify` on the command line, "Decode Audio" in the detail window (which also measures the audio). The tree is the same either way.
- It decodes lossless and hybrid streams (integers of 1 to 32 bits, floats, up to the channels a stream may have) and DSD (stored, fast and high modes). The syntax of the sub-blocks is read by the parser's code (`BlockContents`); the decoder adds the entropy decoding, the decorrelation passes, joint stereo, the shift and the sent, zero, one and duplicate bits, the restoring of floats, and the checksums.
- A hybrid file is decoded to the lossy audio the main file holds where no `.wvc` is beside it or the host is given none; with a `.wvc` that belongs it is decoded to the lossless audio (see the pair above).
- Four checksums are declared and tallied:

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| block checksum | the coded data | the rule of the next row holds and every audio block has one (a block that says it has one and has none counts as failed) |
| block header CRC | the decoded audio | no gap in the block indexes, no block the parser could not read, no broken set and the samples of the blocks match the total the stream states |
| extended CRC | the decoded audio of floats and of integers above 24 bits | never (it covers the extra bits of the blocks that have them only) |
| MD5 | the decoded audio | always; it is compared only when the decoder decoded every block, nothing is missing, the stream is lossless and no set is over the ceiling |

- A stream is verified when every stored checksum was compared, none failed, and at least one compared checksum covers all the audio. A hybrid file without its `.wvc` is verified as its lossy stream by the CRCs of its blocks (`IsLossy`); its MD5 is of the lossless audio and is not compared (`wavpack.md5-needs-correction-file`).
- The MD5 is over the samples in the layout of the source (the new config block's qmode): little-endian by default, big-endian for AIFF and CAF sources, signed bytes, unsigned words, reordered channels, and for DSD the bytes in blocks and the bit order of the source. The last 16-byte MD5 sub-block of either kind is taken, as libwavpack does when it opens a file with `OPEN_ALT_TYPES`. The layouts are libwavpack's (`pack_utils.c`, `wvunpack.c`).
- Findings from the decoder: `wavpack.correction-block-missing` (a hybrid block of a pair decoded without a correction block; libwavpack counts it as an error), `wavpack.audio-crc-mismatch` (the CRC of the decoded audio of a block differs from the stored one), `wavpack.md5-mismatch`, `wavpack.md5-needs-correction-file`, `wavpack.md5-set-too-large`, and the host's `decode.*` findings. A block the parser could not read, or that libwavpack's decoder refuses, is handed as unreadable and counts as not decoded.
- Decoding starts at the first audio block that verifies, as libwavpack opens a file; the audio before it, and the sets that begin without the initial block flag, are handed to the decoding as unreadable units, so the measurements count their samples as lost. A stream whose first configured block libwavpack refuses (a decorrelation term of 0, a sub-block of the wrong size, a DSD high block that is too short) cannot be decoded: its decoding is `NotPossible`.
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
- Decoding: `wavpack.correction-block-missing`, `wavpack.audio-crc-mismatch`, `wavpack.md5-mismatch`, `wavpack.md5-needs-correction-file`, `wavpack.md5-set-too-large`.
- Matroska: `wavpack.matroska-packet-invalid`, `wavpack.matroska-packet-cut`, `wavpack.matroska-block-invalid`, `wavpack.payload-not-analyzed`.

## WavPack 1 to 3

The files WavPack 1.0 to 3.97 wrote from 1998 to 2003. The design, with the sources and what the fixtures showed, is [wavpack-legacy.md](../design/wavpack-legacy.md); the article is `wavpack-legacy`.

### Detection

- The WavPack parser takes both generations; `--format WavPack` reads either.
- **A file of these versions** starts with the source WAV file's header up to its data chunk's header, and the WavPack header follows at once: class 100 where a header of version 1 to 3 with the bytes its version needs follows, class 80 for one of another version or one the data cuts short.
- **A raw file** (version 3, the raw flag, a size of 28) starts with the WavPack header: class 100; class 80 for the other raw headers (a version of 1 or 2, a raw flag that is clear, a size that is not 28, a header the data cuts short).
- **The WAV parser steps back:** a file whose first data chunk starts with `wvpk` is rated 70 there, below the WavPack parser's, with a note. Its own analysis, forced with `--format WAV`, is unchanged.
- **Version 4 does not compete:** a stream of version 4 or 5 starts with a block header whose version is 0x402 to 0x410.
- Tags in front (ID3v2) and behind (APEv2, ID3v1) are the host's; libwavpack's test of the first byte (`R`) is not copied, so a file behind an ID3v2 tag is read.

### What is read

- **The stored WAV header:** the RIFF chunk, every chunk up to the data chunk's header with its ID and size, the format chunk's fields, a lossyWAV `fact` chunk (read with the shared `FactText`) and the size of the source's audio.
- **The WavPack header:**
  - the signature, size and version, in 10 bytes (12 in version 2, 36 in version 3);
  - `bits`, named by what it is: the limit on the bits of a sample in the older lossy mode (Lossy Bit Limit, 0 for a lossless file), or in hybrid mode the target in bits per sample times 256 (Hybrid Target);
  - in version 3 the 16 flags as typed fields (the three never stored among them), the shift, the total samples, the two checks (`crc`, and `crc2` with a correction file; shown, compared only by a decoder), the source's extension and the bytes after a raw file's last sample.
- **The bitstream** as one node. Its end is known only to a decoder; without decoding it ends at the trailer, or at the end of the data. When the file is decoded, the place of the last sample's last bit is compared with it (`wavpack.legacy-bitstream-end-differs`).
- **The trailer:** 16 bytes of 0xFF followed by chunk headers that chain to the end of the data, as 3.4 writes the source's chunks after the audio. The first 64 chunks are listed.
- **The stream:** codec "WavPack lossless", "WavPack hybrid" (a target and the new high flag) or "WavPack lossy" (a bit limit without it), as for version 4; the rate, channels and bits from the stored WAV header (a raw file: 44,100 Hz, 16 bits, or 24 for 3-byte samples, and the channels from the mono flag); the samples from the header (version 3) or from the data chunk's size (versions 1 and 2); the center for one channel and left/right for two.

### Statistics

The names of version 4 are kept where the concept is the same:

- **Compression Mode:** by the flags, as libwavpack's open does: very fast (fast flag), fast (neither the fast nor the high flag), normal (high), high (high with the extreme decorrelator); a hybrid file is normal, and high with the extreme decorrelator. Versions 1 and 2 have one mode, which the rule calls fast.
- **Hybrid Target Bitrate** (bits per sample of one channel) and **Hybrid Target Rate** (kbps), **Lossy Bit Limit** (bits), **Stereo Coding**, **Source Extension**, **IsLossless**, **IsHybrid**, **IsNoiseShaped**, **IsRaw**, Average Bitrate over the bitstream.
- **Stereo Coding** is one value for the stream (there are no blocks to count): "Mid/side stereo" (versions 1 and 2, the lossless fast mode, and joint stereo with the high or hybrid modes), "Left/right stereo" (the lossy and hybrid modes otherwise, and the high mode without joint stereo) or "Adaptive stereo" (the lossless modes that choose sum and difference, left or right sample by sample). The design called the last "Adaptive"; the value is worded like the others.
- No tally, series or graph is built: one bitstream has no units to vary over.

### Encoder detection

- **Encoder:** "WavPack 1", "WavPack 2" or "WavPack 3", medium confidence, from the version field, which libwavpack reports as the major version of the program that wrote the file. Nothing in a file names the release; the Cool Edit filter of the 3.97 sources writes a version 3 header too.
- **Processor:** a `fact` chunk with lossyWAV's text in the stored header names lossyWAV at high confidence, as in WAV files.
- **Writer:** an APEv2 tag's claim, weighed as for version 4.
- **Verification:** a version 3 file declares its `crc` as a checksum ("audio CRC"), compared when the file is decoded; without decoding the status is unknown. Versions 1 and 2 store none, so they are verified only as decoded without error and to the stated length. A hybrid file decodes to its lossy audio where no correction file is read, so it is reported as lossy, and its second CRC, of the lossless audio, is not compared (`wavpack.legacy-crc2-needs-correction-file`, info). With a correction file the checksum is the "correction CRC" instead (below).

### Hybrid mode and the correction file

- **Where it is asked for:** a version 3 file with a bit limit or hybrid target and the new high flag asks the host for `x.wvc`, as libwavpack's open does (`unpack3_open.c`; a file the decoder refuses asks for none). The 3.4 and 3.97 encoders write the same `.wv` with and without `-c`, so nothing in the file says a `.wvc` exists: a missing one is `missing`, and `NotWritten` does not occur.
- **The correction file has no structure:** it is the correction bitstream alone, with no header or signature, so a `.wvc` of version 3 opened alone is not recognized, and beside its file the parser can only tell that it is empty (`notCorrectionData`, `correction.inconsistent`; an empty file is not handed to the decoder, which decodes the lossy audio).
- **Its tree** in the report of its own (`Report.Companions`): one node, "Correction Bitstream", over all the file's bytes, the same with and without decoding; where the decoder found the end of the stream's samples before the file's end, it says so (`wavpack.legacy-correction-unread`, info, with the count). The stream is "WavPack correction data" (`IsCorrectionFile`).
- **The pairing's basis is `DecodedAudio`:** without decoding it is `unchecked` (`correction.unchecked`: "a version 3 correction file has no header; only decoding with it compares it"). With decoding the decoder reads the correction bitstream beside the main one along `unpack3.c`'s `WVC_FLAG` paths and compares the header's second CRC (`unpack3.c`: `crc2` where there is a correction file, else `crc`); the parser makes the pair `consistent` where it holds and the correction bitstream did not end before the samples did, and `inconsistent` otherwise (`checksumsFail`: the CRC differs, `wavpack.legacy-crc2-mismatch`; `cut`: the correction bitstream ends early, `wavpack.legacy-correction-ended`; both point to the header's Correction CRC field).
- **The verification** of a pair is the tally "correction CRC", and reads "with correction file", not "lossy stream"; a correction file that does not hold with its file still decodes (libwavpack uses it as well) and fails that CRC. `wavpack.legacy-crc2-needs-correction-file` is raised only where no correction bitstream was read: a missing, unchecked or empty companion.
- **The decoder** reads the correction after each word of the new high mode (`get_word4`: the position inside the range the lossy word left), adds it to the sample after the decorrelation passes (before the mid/side step of joint stereo), applies the first-order noise shaping with the carried error where the noise-shaped flag is set, and does not clamp the sample, as `unpack_samples3` does. Its bytes come with each frame beside the main bitstream's (`CodedFrame.Correction`), in step with the main bitstream's progress; they count in the decoding allowance and the correction's reading costs 24 work units a sample of a channel (`WorkPerCorrectedSample`).
- **Where it departs from libwavpack:** libwavpack mutes a block whose samples are far out of range (`ATTEMPT_ERROR_MUTING`), which the analyzer does not; it opens a correction file of no byte and reads 0xFF from it, which the analyzer treats as no correction data.
- **Cross-check:** the encoder-written pairs (3.4 and 3.97) decode to the audio of each release's own unpacker given its `.wvc` and equal libwavpack's pass; the hand-built pairs (`built-legacy-pair-*`: a cut `.wvc`, a flipped bit, the `.wvc` of another file, an empty one and 16 bytes after the bitstream) are held to the errors libwavpack counts (`crosscheck_wavpack_legacy_fixtures.py`). No fixture has a mono pair or noise shaping with a correction file (the encoders shape from 64 kHz, and the pairs are 44.1 kHz stereo): those paths follow `unpack3.c` and are not exercised by a file.

### Findings

- Reused from version 4: `wavpack.version-unsupported` (a header behind the data chunk's header with a version other than 1 to 3), `wavpack.flags-invalid` (once for each problem libwavpack refuses: a never-stored flag with the extreme flag, a hybrid target with the new high flag and the fast or high flag, a bit limit or target with the cross-channel flag), `wavpack.total-samples-mismatch` (the header against the data chunk's size), `wavpack.wrapper-fact-text-unrecognized`.
- New: `wavpack.legacy-header-truncated`, `wavpack.legacy-header-size` (a size other than the header's less 8), `wavpack.legacy-wav-format-unsupported` (once for each of: a format chunk under 16 bytes, no format chunk, a chunk before the data over 1 MiB, the format tag, the channels, the rate, the bits, the block align), `wavpack.legacy-channels-mismatch` (the mono flag against the stored header's channels), `wavpack.legacy-bitstream-missing`, `wavpack.legacy-trailer-unrecognized` (16 bytes of 0xFF with no chunk headers after them), `wavpack.legacy-extra-bytes-invalid` (a raw file that says more than 3 bytes follow its last whole sample).

### Where it departs from libwavpack

- libwavpack reads these files only when built with its legacy decoder, which `wvunpack` of the Debian packages of 5.6.0 and 5.9.0 is not.
- It takes the total samples from the data chunk's size and refuses raw files; this parser reports the header's count (as 3.97's `wvunpack` does) and reads raw files.
- It decodes no version 1 mono file; the parser reads its header like any other.

### Not covered

- The audio, when the front end asks for decoding ([the design](../design/wavpack-legacy.md#decoding-decision-5)): the decoder (`Encspect.Formats.WavPack.Decoding`, ID `wavpack`) follows libwavpack 5.6.0's `unpack3.c` (3.97's decoder for version 1 mono), in batches of 4,096 samples charged to the work budget before they are decoded. It reports the CRC that does not hold (`wavpack.legacy-crc-mismatch`), a bitstream that ends before the stated samples (`wavpack.legacy-data-ended`), a bitstream whose last bit lies elsewhere than the parser placed the trailer (`wavpack.legacy-bitstream-end-differs`) and the second CRC of a hybrid file that is not compared where no correction file is read (`wavpack.legacy-crc2-needs-correction-file`); with one it compares that CRC (`wavpack.legacy-crc2-mismatch`, `wavpack.legacy-correction-ended`). The decoded PCM of the encoder-written files equals each release's own unpacker's output (WVUNPACK 1.0 to 3.4 under DOSBox, wvunpack 3.97 on Windows) in `WavPackLegacyDecoderFixtureTests`.
- The `.wvc` of version 3 opened alone: it has no header or signature, so it is not recognized; beside its `.wv` it is read as above.
- Self-extracting files (`-e` of 3.97), and files written by 3.97, which no fixture holds: Wine does not run in the container that made the fixtures, so what 3.97 writes is known from its source only.

## Not covered

- Playback and any output other than the checksum comparison (the PCM sink feeds the measurements and tests, not an export). DSD is decoded to its 1-bit samples for the CRC and the MD5, not to PCM.
- Zeroing or concealing a block that fails its CRC.
- WavPack hybrid correction data in Matroska (BlockAdditions, BlockAddIDType 1) is not read; the stream is reported from its main blocks.
- WavPack in Matroska: Verified Block Checksums is not reported, since the block checksum covers the block's header, which Matroska does not keep; the other figures of a file (Stereo Coding, Wasted Bits and the low-confidence lossyWAV claim, counted from each packet's blocks at the position of the samples before it, the bitrate series and statistics, the block set figures, Bits Per Sample, IsLossless, Decorrelation Passes, the compression mode, Channel Layout, the encoder) are reported from the flags and sub-blocks the packets keep.
- WavPack in Matroska: the lost samples of a packet are the samples its first four bytes declare, so a packet too short to hold them is not counted, and a packet without a block is not counted in the samples either.
- WavPack 1 to 3: no bitrate over time. The bitstream has no blocks or sub-block lengths, so the bits a second of audio takes are known only by decoding, and the decoder, which reads one bitstream sample by sample, does not add series to the report (a decoder never does); the average bitrate is the stream's size over its duration. Without its correction file a hybrid file is decoded to its lossy audio; see [Not covered under WavPack 1 to 3](#wavpack-1-to-3).

## Articles

- **Articles:** `wavpack-blocks`, `wavpack-sub-blocks`, `wavpack-hybrid-mode`, `wavpack-decorrelation`, `wavpack-dsd`, `wavpack-float-and-large-integers`, `wavpack-wrapper`, `wavpack-legacy`.
- **Shared with other formats:** `mid-side-stereo`, `crc`, `bitrate-modes`, `encoder-detection`, `lossywav`.

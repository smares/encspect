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
- What the detailed blocks list and keep is capped per block (1,024 sub-blocks, 64 KiB of data) and across all of them: 65,536 listed sub-blocks and 16 MiB of kept data in all. The sub-blocks past a cap are counted as "Further Sub-blocks". A crafted file therefore costs a bounded amount of memory, whatever the number of its blocks and sub-blocks.

## Reported

- the sample rate (a custom rate from its sub-block, ignored where it is 0 as libwavpack ignores it; for DSD the native bit rate, held to 2,147,483,647 Hz), channels and layout, bits per sample, the total samples and the duration;
- "WavPack lossless", "WavPack hybrid", "WavPack lossy" or "WavPack correction data" as the codec;
- the hybrid target bitrate in bits per sample, from the profile's accumulator ("or less" where the accumulator is 0, since the encoder stores 0 for any target below 568/256 bits per sample, about 2.22; for a false stereo block, whose accumulator is worked out from twice the target, below 540/256, about 2.11), and the average bitrate, which counts the bytes of the audio blocks less the wrapper sub-blocks (the source file's header and trailer) they hold;
- the bitrate per block and per second, as series, with the smallest and largest block, a distribution and the bitrate mode: variable or constant for two sets or more, unknown for a single set, which shows no variation (`IsVbr` is false then);
- the bits per sample as the sample size less the left shift; a shift that leaves no bits is a finding and the bit depth is then not reported.

## Hybrid mode and correction files

- A hybrid `.wv` is lossy by itself. A `.wvc` file holds the correction that makes it lossless: its blocks are read the same way and the stream is reported as correction data.
- A `.wvc` opened alone is a finding (`wavpack.correction-file`), not an error.
- The extra bitstream (wvx) that 32-bit integer and float data need can sit in the `.wv` or in the `.wvc` block; a block that holds only that is not a block without a bitstream.
- The analyzer reads the file it is given. It does not look for the other file of a pair.
- `IsCorrectionFileCreated` in the statistics is true for a correction file, and for a stream whose configuration block says the encoder wrote one (`CreatesCorrectionFile`); it does not say that the file is next to this one.

## Channel layout

- From the channel mask of the channel info sub-block (WAVEFORMATEXTENSIBLE speaker bits).
- Without a mask: mono and stereo from the flags, and discrete channels otherwise. A channel info of a count of 0 and no mask is accepted by libwavpack and counts as none.
- libwavpack keeps the first channel info that gives channels and checks it fully (the count against the streams, the speakers of the mask against the count). A later one only has to be 1 to 7 bytes long. One that fails the other checks is therefore not reported as refused; one that gives other channels or another mask is `wavpack.channel-info-ignored`.
- The channel identities name the channels after the ones the mask names, as libwavpack lists them (`WavpackGetChannelIdentities`): with a mask of two speakers, the first identity is channel 3.
- A set of blocks has at most as many blocks as libwavpack reads into one: the streams the long form of the channel info states (up to 4,096), else 8. A set that has more without a final block is ended there (`wavpack.block-set-broken`).
- A channel count in the channel info that differs from the channels of the first set of blocks is a finding (`wavpack.channel-count-mismatch`).

## Encoder detection

- "WavPack 5.0 or later" where the blocks hold a new configuration block or a block checksum, which libwavpack has written since 5.0, as libwavpack tells encoder versions 5 and 4. The confidence is medium: the library writes no encoder name. FFmpeg 6.1.1's encoder writes neither.
- "WavPack 4 stream, no WavPack 5 features" for the others, with low confidence: libwavpack reports encoder version 4 for them, but other encoders write such streams too (FFmpeg's writes version 0x410 with no new configuration block and no checksums). The evidence says so.
- An ID3v2 TSSE frame weighs in as a claim of the encoder role. A claim that names WavPack is evidence; one that names FFmpeg is evidence for a WavPack 4 stream, which FFmpeg writes; a claim that names anything else is a finding (`wavpack.encoder-claim-differs`).
- An APEv2 Encoder item (the key in any case, which FFmpeg writes as `encoder` with its Lavf version) and ffmpeg's muxer version name the writer, with low confidence, apart from the blocks' encoder; two that name different software are `wavpack.writer-claim-differs`, and software known to write another format (LAME on a WavPack stream) is `wavpack.writer-other-format`.
- Configuration flags are shown as the command line options that set them (`-f`, `-h`, `-hh`, `-x`, `-c`, `-m`).
- Observed in Encspect's test files, and not used by the analyzer:
  - the first audio block of each of the 53 `.wv` files from wavpack 5.6.0 holds a config block, a new config block and a block checksum;
  - the one `.wv` file from FFmpeg 6.1.1's encoder holds none of the three;
  - the 8 `.wvc` correction files hold a block checksum and neither of the others.
- That is one file of FFmpeg's encoder and none of a libwavpack older than 5.0, so the absence of a config block is not a rule: the analyzer keeps the low-confidence "WavPack 4 stream" and says in its evidence that another encoder may have written it.

## Tags

- APEv2 and ID3v1 tags after the last block are the host's, as are ID3v2 tags in front. An ID3v2.4 SEEK frame that points between blocks ends the stream there, whether or not the data starts with a block, and the bytes after the tag are reported as data (`wavpack.data-after-tag` where the stream declares no end).
- A tag candidate inside the chain of blocks belongs to the stream.

## Findings

- A code that stands for several problems is reported once for each problem (`wavpack.subblock-size` once for each kind of sub-block, for instance), each with its count, so the message of a finding never names a problem of another.
- Blocks: `wavpack.version-unsupported`, `wavpack.flags-invalid` (the mono and false stereo flags both set, a left shift not less than the sample size), `wavpack.reserved-flag`, `wavpack.block-size-mismatch`, `wavpack.block-truncated`, `wavpack.block-trailing-byte`, `wavpack.block-checksum-mismatch`, `wavpack.block-checksum-missing`, `wavpack.data-skipped`.
- Sub-blocks: `wavpack.subblock-overrun`, `wavpack.subblock-size` (including decorrelation weights or samples before any decorrelation terms, and a new config block that reorders more channels than its layout has), `wavpack.subblock-unknown`, `wavpack.optional-subblock-unknown`, `wavpack.decorr-terms-invalid`, `wavpack.channel-info-invalid`, `wavpack.channel-info-ignored`, `wavpack.dsd-invalid` (including a stored mode whose audio is not exactly the bytes the block counts, a fast mode that ends right after the history bits, and a high mode with fewer than 13 (mono) or 20 (stereo) bytes after the mode or a rate step other than 20), `wavpack.dsd-flag-mismatch`, `wavpack.sample-rate-missing`, `wavpack.sample-rate-zero`, `wavpack.sample-rate-implausible` (a DSD bit rate over 2,147,483,647 Hz), `wavpack.audio-without-bitstream`.
- Stream: `wavpack.no-blocks`, `wavpack.no-audio`, `wavpack.first-block-index`, `wavpack.block-index-gap`, `wavpack.block-index-overlap`, `wavpack.block-set-broken`, `wavpack.total-samples-mismatch`, `wavpack.total-samples-unknown`, `wavpack.format-changes`, `wavpack.mixed-streams`, `wavpack.channel-count-mismatch`, `wavpack.correction-file`, `wavpack.encoder-claim-differs`, `wavpack.writer-claim-differs`, `wavpack.writer-other-format`.

## Not covered

- The coded samples (planned: entropy pass, and phase 3's checksum-verifying decoders): no entropy decoding, no decorrelation, no DSD decoding. A file whose bitstream is damaged but whose blocks are intact shows no finding unless the checksums or the MD5 are checked by a decoder.
- The MD5 checksum is shown, not verified, since that needs the decoded samples (planned in phase 3).
- The legacy format of WavPack 1 to 3 (`.wv` files without the "wvpk" blocks; planned: later, a separate item).
- Finding the `.wvc` that belongs to a `.wv` (planned: later, needs host access to a companion file).

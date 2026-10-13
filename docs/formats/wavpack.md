# WavPack

WavPack 4 and 5 files: `.wv` (lossless, hybrid and DSD) and the `.wvc` correction files of hybrid mode.

## What is read

- Every block, by its 32-byte header:
  - the signature, the size, the version (0x402 to 0x410), the 40-bit block index and total samples, the sample count, the flags and the CRC;
  - every flag as a typed field with a description: bytes stored, mono, hybrid, joint stereo, cross-channel decorrelation, noise shaping, float data, integer shift, the sample rate, DSD and the rest.
- Every metadata sub-block of a block, named by its ID. The structured ones are decoded:
  - decorrelation terms, weights and samples, entropy variables and the hybrid profile;
  - noise shaping weights, float and extra-bits information;
  - the channel layout (channel info, channel identities, the channel mask), the DSD sub-block's mode;
  - the configuration block (modes, the hybrid bitrate) and the new configuration block;
  - the MD5 checksum, the block checksum and the RIFF, AIFF or CAF wrapper the encoder kept (header and trailer, with their chunks).
- The block checksum is recomputed and compared; a missing or wrong one is a finding.
- Blocks of one multichannel set (the initial and final block flags) and their block indexes, which must follow without gaps or overlaps.
- Runs of bytes that are no block are shown as skipped, and the reader finds the next block as libwavpack does: a header counts when its size, version and sample count are plausible.
- Once 4,096 blocks are detailed, runs of further blocks that add nothing become one node each. Sub-block lists and stored bytes are capped too, so a crafted file costs a bounded amount of memory.

## Reported

- the sample rate (a custom rate from its sub-block; for DSD the native bit rate), channels and layout, bits per sample, the total samples and the duration;
- "WavPack lossless", "WavPack hybrid", "WavPack lossy" or "WavPack correction data" as the codec;
- the hybrid target bitrate in bits per sample, from the profile's accumulator, and the average bitrate;
- the bitrate per block and per second, as series, with the smallest and largest block, a distribution and whether the bitrate varies.

## Hybrid mode and correction files

- A hybrid `.wv` is lossy by itself. A `.wvc` file holds the correction that makes it lossless: its blocks are read the same way and the stream is reported as correction data.
- A `.wvc` opened alone is a finding (`wavpack.correction-file`), not an error.
- The extra bitstream (wvx) that 32-bit integer and float data need can sit in the `.wv` or in the `.wvc` block; a block that holds only that is not a block without a bitstream.
- The analyzer reads the file it is given. It does not look for the other file of a pair.

## Channel layout

- From the channel mask of the channel info sub-block (WAVEFORMATEXTENSIBLE speaker bits).
- Without a mask: mono and stereo from the flags, and discrete channels otherwise.
- A channel count in the channel info that differs from the channels of the first set of blocks is a finding (`wavpack.channel-count-mismatch`).

## Encoder detection

- "WavPack 5.0 or later" where the blocks hold a new configuration block or a block checksum, which WavPack 5.0 added, and "WavPack 4" for the others, as libwavpack tells encoder versions 5 and 4. The confidence is medium: the library writes no encoder name.
- A claim in the APEv2 tag that names another encoder is a finding (`wavpack.encoder-claim-differs`).
- Configuration flags are shown as the command line options that set them (`-f`, `-h`, `-hh`, `-x`, `-c`, `-m`).

## Tags

- APEv2 and ID3v1 tags after the last block are the host's, as are ID3v2 tags in front. An ID3v2.4 SEEK frame that points between blocks ends the stream there, and the bytes after it are reported as data.
- A tag candidate inside the chain of blocks belongs to the stream.

## Findings

- Blocks: `wavpack.version-unsupported`, `wavpack.flags-invalid`, `wavpack.reserved-flag`, `wavpack.block-size-mismatch`, `wavpack.block-truncated`, `wavpack.block-trailing-byte`, `wavpack.block-checksum-mismatch`, `wavpack.block-checksum-missing`, `wavpack.data-skipped`.
- Sub-blocks: `wavpack.subblock-overrun`, `wavpack.subblock-size`, `wavpack.subblock-unknown`, `wavpack.optional-subblock-unknown`, `wavpack.decorr-terms-invalid`, `wavpack.channel-info-invalid`, `wavpack.dsd-invalid`, `wavpack.dsd-flag-mismatch`, `wavpack.sample-rate-missing`, `wavpack.audio-without-bitstream`.
- Stream: `wavpack.no-blocks`, `wavpack.no-audio`, `wavpack.first-block-index`, `wavpack.block-index-gap`, `wavpack.block-index-overlap`, `wavpack.block-set-broken`, `wavpack.total-samples-mismatch`, `wavpack.total-samples-unknown`, `wavpack.format-changes`, `wavpack.mixed-streams`, `wavpack.channel-count-mismatch`, `wavpack.correction-file`, `wavpack.encoder-claim-differs`.

## Not covered

- The coded samples: no entropy decoding, no decorrelation, no DSD decoding. A file whose bitstream is damaged but whose blocks are intact shows no finding unless the checksums or the MD5 are checked by a decoder.
- The MD5 checksum is shown, not verified, since that needs the decoded samples.
- The legacy format of WavPack 1 to 3 (`.wv` files without the "wvpk" blocks).
- Finding the `.wvc` that belongs to a `.wv`.

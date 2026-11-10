---
id: wasted-bits
title: Wasted bits
related: flac-subframes, bit-depth-usage, low-bits-per-span, lossywav, wavpack-float-and-large-integers
references: RFC 9639 (FLAC), section 9.2; WavPack's INT32_INFO sub-block (Zero Bits); the project's fixtures, tests/fixtures/*/*lossy*
---

**The problem.** Some audio has low bits that never change. Audio that was scaled up from a lower bit depth, or that came from a source with fewer bits, has zeros in the bottom of every sample, and coding them is wasted effort.

**How it works.** A subframe has a flag for wasted bits. When it is set, a count follows in unary, and the encoder has removed that many low bits from every sample of the subframe, because they were all zero. The decoder shifts the samples left by the count after it has rebuilt them. The count is the number of trailing zero bits that all its samples share.

**What you see.** Wasted Bits in each subframe, and in the statistics how many subframes use them. A stream that has any also gets a Wasted Bits statistic: how many subframes have each count, and the changes, where a channel's count differs from its previous one. Subframes of digital silence are listed apart. Subframes are counted as coded, so a side channel stays the side channel. WavPack's Zero Bits is the same count, once per block for all its channels, with the same statistic. A file whose subframes all report the same number of wasted bits has less real depth than its header says.

**A pattern.** The counts of one channel usually change little from frame to frame. [lossyWAV](lossywav)'s output is different: its count changes with the audio, block by block. In Encspect's test files, FLAC codings of lossyWAV 1.4.2's output in frames as long as its block have 65 to 103 changes in about a second of audio, where the other FLAC test files recoded in frames of 512 have at most 3, apart from a simulated tool that zeroes bits per 4,096 samples (58, by construction). The analyzer uses this pattern, with a grid test, for its low-confidence claim.

**Measured too.** When the audio is decoded and measured, [Low Bits Unused](bit-depth-usage) counts the same thing over every sample of the stream instead of per subframe. The same count is taken for each span of 32 samples in [low bits unused per span](low-bits-per-span).

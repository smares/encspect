---
id: bit-depth-usage
title: Bit-depth usage
related: levels, wasted-bits, pcm-samples, digital-silence
references: Encspect design, docs/design/spectral-analysis.md (Levels); RFC 9639 (FLAC), section 9.2
---

**The problem.** A stream declares how many bits each sample has, but the samples may use fewer. Audio that came from fewer bits, or was scaled up by a power of two, has low bits that are 0 in every sample.

**How it works.** For integer samples the analyzer combines every sample of the stream with OR and counts the low bits of the result that are 0: these are the low bits that are never set, reported as Low Bits Unused out of the declared bits, together with the highest magnitude reached. A single sample with its lowest bit set makes the count 0. A stream in which every sample is 0 has no count, because no sample has a bit set.

For floating-point samples, which have no integer bits, two figures say whether every sample is a whole multiple of 2^-15, or of 2^-23, within [-1, 1): the grids of 16-bit and 24-bit integer audio. They are figures of the samples, not labels, and a sample of exactly +1.0 is outside the range.

**What you see.** In Encspect's test files, 16-bit noise stored as 24-bit samples shifted left by 8 bits reads 8 of 24 low bits unused, and a second channel shifted by 12 reads 12.

**Coded counterpart.** FLAC stores a count of wasted bits for each subframe ([wasted bits](wasted-bits)); this figure is counted over the decoded samples of the whole stream and does not depend on how a file was coded.

---
id: low-bits-per-span
title: Low bits unused per span
related: bit-depth-usage, wasted-bits, lossywav, levels, digital-silence
references: Encspect design, docs/design/lossywav-detection.md (The measurement); the fixtures tests/fixtures/measurement/synthetic-lowbits-* and tests/fixtures/flac/external-lossyflac-*
---

**The problem.** [Low Bits Unused](bit-depth-usage) is one number for the whole stream: a single sample with its lowest bit set makes it 0. Some audio has low bits that are 0 only for a stretch, which the stream figure cannot show.

**How it works.** Each channel is cut into spans of 32 consecutive samples, on the stream's positions that are multiples of 32. A span's count is the number of low bits that are 0 in all its samples; its magnitude is the most bits any sample needs besides the sign. Encspect reports:

- the number of spans with each count, all channels together, and the share above the stream's Low Bits Unused;
- the changes: spans whose count differs from the same channel's previous span, and the moments, the positions at which some channel changes;
- the grid: the largest power of two from 256 to 65,536 samples such that at least 95% of the moments fall within one span of the phase with the most moments, given at least 20 moments;
- for several channels, the share of times at which they have the same count.

A span of silence has no count and is listed apart. A short span (fewer than 32 samples: the end, a gap, withheld output) and a thin span (fewer than 4 bits of magnitude above its count) are counted in the distribution but take no part in the changes. The 20 moments, the 95%, the 4 and the range of grids are conventions of this analysis.

**What you see.** In Encspect's test files, 16-bit audio in 24 bits has one count and no change, and audio truncated to 14 bits has no change, with or without dither. [lossyWAV](lossywav) 1.4.2 output (standard preset, noise floor at -100 dBFS, 44.1 kHz) has counts from 0 to 5 and 66 changes, all on a grid of 512 samples. Cutting its first 100 samples keeps the grid, a gain of -0.5 dB leaves no change, and joining two outputs shifted by 100 samples leaves 71 changes and no grid.

**Limits.** The figures say nothing about why a count is what it is.

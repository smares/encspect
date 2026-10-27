---
id: digital-silence
title: Digital silence
related: levels, clipping, bit-depth-usage
references: Encspect design, docs/design/spectral-analysis.md (Levels)
---

**The problem.** Digital silence is audio in which the samples are exactly 0. It differs from quiet audio, which has noise or dither and never reads exactly 0.

**How it works.** The analyzer counts the sample frames in which every measured channel is 0, and reports how many start the stream without a break, how many end it and how many there are in all. The count in all includes zeros anywhere, so a sine wave reads the few samples that fall on its zero crossings; the stretches at the start and at the end are the ones that show silence. When every sample is 0 the three counts are equal. The report also keeps these three counts for each channel, so a channel that is silent while the others are not shows there.

**Gaps.** Nothing is counted across a gap in the decoded blocks, where frames were lost: a stretch of zeros before a gap and one after it are two stretches, and a stretch at the start ends at the gap.

**What you see.** Digital Silence in samples and, where the sample rate is known, in seconds. In Encspect's test files, 8,192 zero samples read 8,192 at the start, at the end and in all, and noise that never reaches 0 with 1,000 zeros at the start, 500 in the middle and 2,000 at the end reads 1,000, 2,000 and 3,500.

**What it shows.** Where the stream carries no signal in the samples it was decoded to. The figure does not say whether the silence was in the source or was made later.

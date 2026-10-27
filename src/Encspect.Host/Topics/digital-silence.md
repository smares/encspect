---
id: digital-silence
title: Digital silence
related: levels, clipping, bit-depth-usage
references: Encspect design, docs/design/spectral-analysis.md (Levels)
---

**The problem.** Digital silence is audio in which the samples are exactly 0. It differs from quiet audio, which has noise or dither and never reads exactly 0.

**How it works.** The analyzer finds the sample frames in which every measured channel is 0 and reports how many of them start the stream without a break and how many end it: the digital silence. It also reports Zero Samples, the count of all such frames wherever they are. That count includes the zeros of quiet audio and a sine wave's zero crossings, so it is a count of samples and not a stretch of silence. When every sample is 0 the three counts are equal. The report also keeps the counts for each channel, so a channel that is silent while the others are not shows there.

**Gaps.** Nothing is counted across a gap in the decoded blocks, where frames were lost: a stretch of zeros before a gap and one after it are two stretches, and a stretch at the start ends at the gap.

**What you see.** Digital Silence and Zero Samples in samples and, where the sample rate is known, in seconds. In Encspect's test files, 8,192 zero samples read 8,192 at the start, at the end and in all, noise that never reaches 0 with 1,000 zeros at the start, 500 in the middle and 2,000 at the end reads 1,000, 2,000 and 3,500, and the dithered 16 kHz lines 60 dB down read 5,122 zero samples with no silence at either end.

**What it shows.** Where the stream carries no signal in the samples it was decoded to. The figure does not say whether the silence was in the source or was made later.

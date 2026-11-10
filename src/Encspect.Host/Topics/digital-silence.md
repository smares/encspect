---
id: digital-silence
title: Digital silence
related: levels, clipping, bit-depth-usage
references: Encspect design, docs/design/spectral-analysis.md (Levels; Step 6)
---

**The problem.** Digital silence is audio in which the samples are exactly 0. It differs from quiet audio, which has noise or dither and never reads exactly 0.

**How it works.** The analyzer finds the sample frames in which every measured channel is 0 and counts the stretches of them that last at least 10 ms: the sample rate divided by 100, rounded up (441 samples at 44.1 kHz, 80 at 8 kHz). Ten milliseconds is a convention of this analysis, not of a specification. A tone that reaches one least significant bit rounds to 0 only around its zero crossings: for about 8 ms at 20 Hz, and for less above.

The figures are the stretches, the samples in them, the longest, and the stretch at the start and the one at the end, which follow the same rule: a leading or trailing run shorter than the minimum reads none. Zero Samples counts all zero frames wherever they are, with no minimum, so it includes the zeros of quiet audio and a sine's zero crossings. A stream that states no sample rate has no 10 ms, and its stretch figures are not measured.

**Gaps.** Nothing is counted across a gap in the decoded blocks: a stretch before a gap and one after it are two, and each must reach the minimum on its own. NaN and infinite float samples are counted apart and read as 0, so a run of them reads as silence.

**What you see.** Digital Silence in stretches and, where the rate is known, in seconds. In Encspect's test files, noise that never reaches 0 with 1,000 zeros at the start, 500 in the middle and 2,000 at the end reads 3 stretches of 3,500 samples, the longest 2,000; and at 44.1 kHz, 300 zeros before a gap and 300 after it read no stretch, where 500 before the next gap read one.

**What it shows.** Where the stream carries no signal in the samples it was decoded to, not whether the silence was in the source or made later.

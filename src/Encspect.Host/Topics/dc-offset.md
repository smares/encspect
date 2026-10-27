---
id: dc-offset
title: DC offset
related: levels, clipping, digital-silence
references: Encspect design, docs/design/spectral-analysis.md (Levels)
---

**The problem.** The samples of audio swing around a middle value, 0 for signed samples. When their mean is not 0 the whole waveform sits above or below the middle, and one side reaches the limit of the format sooner than the other.

**How it works.** The DC offset of a channel is the mean of its samples, shown as a percentage of full scale and as a level in dBFS, with the sign in the percentage. For integer samples the sum is exact and the mean is rounded to millionths of a percent, so 1,000 on every sample of a 16-bit channel reads 3.051758 percent. For floats the sum is made in double precision in the order of the samples. The stream's figure is that of the channel with the largest offset.

**What you see.** DC Offset in the report, with the level in dBFS and the offset of each channel; in JSON dcOffset in percent and dcOffsetLevel in dBFS. In Encspect's test files, a left channel that alternates between 1,200 and 800 and a right channel that alternates between -200 and -400 read 3.051758 and -0.915527 percent.

**What it shows.** A mean is the average over the whole stream. A short stretch with an offset inside a long stream, or an offset that moves, can average out, so a figure near 0 does not show that no part of the stream has an offset. The figure does not say what caused the offset.

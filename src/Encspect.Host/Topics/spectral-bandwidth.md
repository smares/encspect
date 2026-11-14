---
id: spectral-bandwidth
title: Spectral bandwidth
related: spectral-cutoff, spectrogram, measurements, lowpass, coded-bandwidth
references: Encspect design, docs/design/spectral-analysis.md
---
**The problem.** The [spectral cutoff](spectral-cutoff) measures an edge, a step between two bands. Content can stop at a frequency without a step the cutoff finds. The bandwidth measures where the spectrum meets a floor, steep or gentle.

**How it works.** The floor is the mean level of the 1 kHz of rows that ends at 99 % of half the sample rate, the top of the search. Walking down, the floor's run goes on while the 1 kHz band that ends at each row has a mean within 3 dB of the floor. The bandwidth is the lowest row of the run, where the 1 kHz band under it is at least 20 dB above the floor, with the floor's level. It is measured on the average spectrum of all channels but the LFE and of each channel, from 8,000 Hz up.

**Why a flat run.** A sloping spectrum is not a floor: pink noise falls about 3 dB an octave, so "10 dB above the top" would put its bandwidth near 2 kHz. Its top octave is within 3 dB, and the 20 dB test then fails.

**What it shows.** Content that stops where a flat floor begins: upsampled from a lower rate, a lowpass in an encoder or a mastering chain, an anti-alias filter, or a recording that meets its noise floor. It cannot tell them apart, and it is neither the [coded bandwidth](coded-bandwidth) nor the [lowpass frequency](lowpass) an encoder states. None found says why: the spectrum is flat down to 2 kHz, or it is a slope.

**Observations.** In Encspect's test files, lines in exact bands (16 kHz) read 16,021 Hz, the cutoff's row; pink noise upsampled by the fixture script's windowed-sinc filter from 44.1 to 48 kHz reads 21,516 Hz; LAME 3.100 (-b 64, -b 160) and oggenc 1.4.2 (-q 2) read 0 to 86 Hz above their cutoffs. It is not found for FFmpeg 6.1.1's default resampler from 44.1 to 48 kHz (the spectrum falls about 16 dB from 21 to 23.9 kHz and has no floor), for FFmpeg's aac at 48 and 64 kbps, for white noise, or for pink noise without a lowpass (LAME -V 0, oggenc -q 8).

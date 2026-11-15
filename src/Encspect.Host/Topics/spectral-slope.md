---
id: spectral-slope
title: Spectral slope
related: spectral-cutoff, spectral-bandwidth, spectrogram, measurements, lowpass
references: Encspect design, docs/design/spectral-analysis.md
---
**The problem.** A spectrum can fall all the way to the top of the search without a step the [spectral cutoff](spectral-cutoff) finds or a floor the [spectral bandwidth](spectral-bandwidth) reaches. The slope says where it is still falling, how far and how fast.

**How it works.** The top band is the 1 kHz of rows that ends at 99 % of half the sample rate. The figure is found where the 1 kHz band under it is at least 5 dB above it. The run then goes down while each 1 kHz band stands at least 3 dB above the 1 kHz over it, and stops at 2 kHz. The figure is the run's start (the upper edge of its lowest row), the top of the search, the fall (the mean level of the band at the start less the top band's) and the fall per kHz to the middle of the top band. It is measured on the average spectrum of all channels but the LFE and of each channel, from 8,000 Hz up. The text prints it where no cutoff is found.

**What it shows.** A spectrum still falling where the search ends: a resampler's transition, a lowpass that keeps falling to half the sample rate, an edge within about 2 kHz of the top, or steep content at a low rate, which it cannot tell apart. It is measured at the top of the search, not a line fitted over the whole spectrum, as in music information retrieval.

**Observations.** In Encspect's test files, FFmpeg 6.1.1's default resampler from 44.1 to 48 kHz reads 13.3 dB from 21,328 to 23,766 Hz, 6.8 dB per kHz, with no cutoff and no bandwidth. Pink noise through one and two lowpass sections at 16 kHz reads 35.7 dB from 17,140 Hz and 65.7 dB from 15,504 Hz. FFmpeg's aac at 48 and 64 kbps (the skirt above the cutoff flattens), the soxr engine from 44.1 to 48 kHz and the same resampler from 44.1 to 96 kHz read none. From 48 to 44.1 kHz the default resampler also reads none: its top band is 3.7 dB under the band below, short of the 5 dB start.

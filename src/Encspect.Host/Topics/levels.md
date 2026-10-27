---
id: levels
title: Levels, sample peak and RMS
related: measurements, clipping, dc-offset, digital-silence, bit-depth-usage
references: Encspect design, docs/design/spectral-analysis.md (Levels)
---

**The problem.** A file says how many bits each sample has, not how much of that range the audio uses. The levels answer that from the decoded samples.

**How it works.** Full scale is the largest magnitude a format holds: 2^(bits - 1) for integers, where the most negative code counts as full scale, and 1.0 for floating-point samples. A level is 20 log10 of a ratio to full scale, in decibels relative to full scale (dBFS), rounded to the nearest tenth with halves up.

- **Sample peak:** the largest magnitude of each channel. It is the largest sample, not the largest value of the waveform between samples. A channel whose samples are all 0 has no peak, printed as -inf.
- **RMS level:** the square root of the mean of the squares. A full-scale square wave reads 0 dBFS and a full-scale sine 3.01 dB lower; a convention that gives the sine 0 dB reads every figure 3.01 dB higher.

The stream's peak is the highest of its channels. Its RMS level covers all measured channels together.

**Exactness.** Integer samples are summed exactly in 128-bit integers, so the figures do not depend on the order or the block sizes of the decoder. Float samples that are NaN or infinite are counted and measured as 0, and samples beyond 1,024 times full scale (+60.2 dBFS) are clamped to that and reported as clamped.

**What you see.** Sample Peak and RMS Level in dBFS, for the stream and for each channel.

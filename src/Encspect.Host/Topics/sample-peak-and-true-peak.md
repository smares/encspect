---
id: sample-peak-and-true-peak
title: Sample peak and true peak
related: levels, loudness, clipping, measurements
references: ITU-R BS.1770-5 Annex 2; EBU Tech 3341 section 2.6 and Table 1 (signals 15 to 23)
---

**The problem.** The sample peak is the largest stored sample. The waveform that a converter draws between the samples can be higher: two samples at 0.9 can have a peak of 1.1 between them. A converter, a sample rate conversion or a lossy encoder can clip at such an inter-sample peak even though no sample reaches full scale.

**How it works.** ITU-R BS.1770 Annex 2 estimates the peak of the waveform by oversampling: the signal is interpolated by 4 with a 48-tap lowpass filter, split into four phases of twelve taps, and the largest magnitude is taken. The result is in dBTP, decibels relative to full scale.

- **Below 96 kHz** all four phases run; at 44.1 kHz the oversampled rate is 176.4 kHz.
- **From 96 kHz up to 192 kHz** the Annex asks for less oversampling and gives no filter; the analyzer runs two of the four phases.
- **From 192 kHz** the sample peak is the true peak.

The filter has a little ripple, so a figure can read a few tenths of a dB under or over the waveform's peak; EBU Tech 3341 allows +0.2 and -0.4 dB. The analyzer never reports a true peak below the sample peak.

**What you see.** Maximum True Peak Level in dBTP for the stream and each channel, next to the [sample peak](levels) in dBFS. A true peak above 0 dBTP means the waveform passes full scale between samples; it says nothing about whether a converter clips there.

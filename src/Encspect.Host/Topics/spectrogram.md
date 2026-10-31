---
id: spectrogram
title: Spectrogram and average spectrum
related: measurements, spectral-cutoff, lowpass
references: P. D. Welch, The use of fast Fourier transform for the estimation of power spectra, IEEE Trans. Audio Electroacoust. 15(2), 1967; F. J. Harris, On the use of windows for harmonic analysis with the discrete Fourier transform, Proc. IEEE 66(1), 1978; G. Heinzel, A. Rudiger and R. Schilling, Spectrum and spectral density estimation by the discrete Fourier transform, 2002
---

**The problem.** A waveform shows level over time and hides which frequencies the level is made of. The short-time Fourier transform shows both.

**How it works.** The samples are cut into frames that overlap by half, each multiplied by a periodic Hann window and transformed. A frame lasts 85 to 170 ms: the smallest power of two that holds at least 0.085 s of samples, from 1,024 to 32,768 points, so 4,096 points at 44.1 and 48 kHz. The power of the bins is reduced to 512 rows from 0 Hz to half the sample rate, each the mean power of its bins, and levels are in decibels relative to the power of the bin a full-scale sine is centered on. Because a row is the mean of its bins (4 at 4,096 points, 8 at 8,192, 16 at 16,384), that sine itself reads lower in its row: 10 log10(1.5 / bins) dB where its two neighboring bins are in the row too (-4.3, -7.3 and -10.3 dB at 48, 96 and 192 kHz) and 10 log10(1.25 / bins) where one of them is in the next row (-2.0 dB at 22.05 kHz, where a row has 2 bins). Noise reads lower in a finer bin by 10 log10 of the bin width, so the same noise reads lower with a larger frame.

**What you see.** The average spectrum is the mean power of each row over all frames, in tenths of a decibel. The spectrogram is the same rows over time: at most 1,024 columns, each a whole number of hops, which doubles whenever the stream would need more columns, so any length ends with 512 to 1,024 columns. A cell is a byte, the level in whole decibels plus 200 and limited to 1 to 220, and 0 means no data, as where frames were lost. Column 0 starts at sample floor(firstPosition / hopSamples) × hopSamples, the start of the hop that holds the stream's first sample. The waveform keeps the smallest and largest sample of each channel in each column, in 16-bit sample codes.

A tone shows as a line, a sweep as a slope, noise as a band, and a lowpass as a flat ceiling, which [spectral cutoff](spectral-cutoff) measures.

---
id: measurements
title: Measurements of decoded audio
related: spectral-cutoff, spectrogram, verification, lowpass, coded-bandwidth, bwf-broadcast-extension, replaygain
references: P. D. Welch, The use of fast Fourier transform for the estimation of power spectra, IEEE Trans. Audio Electroacoust. 15(2), 1967; F. J. Harris, On the use of windows for harmonic analysis with the discrete Fourier transform, Proc. IEEE 66(1), 1978
---

**The problem.** A file states many things about its audio in headers, tags and checksums, but some questions only the samples can answer: where the energy of the sound ends in frequency, and how it changes over time.

**How it works.** When the analysis decodes a stream, the host hands every block of decoded samples both to the front end and to a measurer. One decoding pass therefore verifies the checksums and measures, and measuring never changes a verification or a decoding finding. The measurer keeps one frame of overlap and arrays of a fixed size, so a long stream costs time and no more memory. Its figures are integers in fixed units, whole hertz and tenths of a decibel, and its arithmetic does not depend on the platform, so a report reads the same on every machine.

**Which samples.** The samples the decoder wrote, before any trimming: samples that a format drops at the start are measured too, a TrueHD stream is measured as the presentation the decoder decodes, and a WavPack hybrid file without its correction file as the lossy audio it decodes to. Nothing is measured across a gap where frames were lost; the figures say how many samples were skipped. At most 32 channels are measured, and the combined figures leave out the LFE channel.

**Stated and measured.** A figure computed here is measured. A figure that the file writes about itself, such as the loudness of a Broadcast Wave bext chunk or a ReplayGain value, is stated. The two are different things and are never compared or called right or wrong.

**Limits.** Measuring has a work budget and a memory bound of its own. Past the work budget, measuring stops with a finding and decoding goes on; past the memory bound, the streams that no longer fit are not measured, and say so. It is off unless asked for: the command line's --measure.

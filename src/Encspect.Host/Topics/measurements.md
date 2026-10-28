---
id: measurements
title: Measurements of decoded audio
related: spectral-cutoff, spectrogram, levels, loudness, loudness-range, sample-peak-and-true-peak, verification, pcm-samples, lowpass, coded-bandwidth, bwf-broadcast-extension, replaygain
references: P. D. Welch, The use of fast Fourier transform for the estimation of power spectra, IEEE Trans. Audio Electroacoust. 15(2), 1967; F. J. Harris, On the use of windows for harmonic analysis with the discrete Fourier transform, Proc. IEEE 66(1), 1978
---

**The problem.** A file states much about its audio in headers, tags and checksums, but some questions only the samples can answer: where the energy ends in frequency, how loud the sound is, and how that changes over time.

**How it works.** When the analysis decodes a stream, the host hands every block of decoded samples to a measurer as well as to the front end. One decoding pass therefore verifies and measures, and measuring never changes a verification or a decoding finding. The measurer keeps arrays of a fixed size, so a long stream costs time and no more memory. Its figures are integers in fixed units, whole hertz and tenths of a decibel, and its arithmetic does not depend on the platform, so a report reads the same on every machine.

**Which samples.** The samples the decoder wrote, before any trimming (for a WAV, Wave64 or AIFF file, the [samples as stored](pcm-samples)): samples a format drops at the start are measured too, a TrueHD stream as the presentation the decoder decodes, and a WavPack hybrid file without its correction file as the lossy audio it decodes to. Nothing is measured across a gap where frames were lost; the figures say how many samples were skipped. At most 32 channels are measured.

**Which figures.** The spectrum and its cutoff; the [levels](levels): sample peak, RMS, DC offset, clipping runs, digital silence and bit-depth usage; the [loudness](loudness) of ITU-R BS.1770 and EBU R 128 with the [loudness range](loudness-range); and the [true peak](sample-peak-and-true-peak). --measure takes each by name: spectrum, levels, loudness and true-peak.

**Stated and measured.** A figure computed here is measured; a loudness or true peak uses the names of the Broadcast Wave bext fields. A figure the file writes about itself, such as a bext loudness or a ReplayGain value, is stated. The two are never compared or called right or wrong.

**Limits.** Measuring has a work budget and a memory bound of its own: past the first it stops with a finding and decoding goes on, past the second the streams that no longer fit are not measured, and say so. It is off unless asked for: --measure.

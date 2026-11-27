---
id: nyquist-edge
title: Nyquist edge
related: spectral-cutoff, spectral-bandwidth, spectral-slope, lowpass, spectrogram, measurements
references: Hydrogenaudio, Sample Rate Conversion Comparison; SoX 14.4.2, sox --help-effect rate; r8brain-free-src, CDSPResampler.h; Encspect design, docs/design/spectral-analysis.md
---
**The problem.** A converter that raises the sample rate lowpasses at the old rate's Nyquist frequency, half the old rate: what lies above it is not content. Audio from 44,100 Hz taken to 48,000 Hz ends in a steep edge near 22,050 Hz, which the cutoff reports as a frequency only.

**How it works.** For each common rate under the stream's own (8,000, 11,025, 12,000, 16,000, 22,050, 24,000, 32,000, 44,100, 48,000, 88,200, 96,000, 176,400, 192,000 Hz) the analyzer takes the cutoff's step (the mean level of 1 kHz of rows under a row boundary against the 1 kHz over it) at the boundaries from 3 % under that rate's Nyquist frequency up to the first boundary over it. The edge is found where the largest step is 25 dB or more, is a local maximum, is followed by a fall of at most a quarter of the step over the next 1 kHz, and shows in at least half of the frames with signal. Several edges are listed, highest first. Only the combined spectrum is searched. Gentle converters leave a slope instead (see the [spectral slope](spectral-slope)).

**The level above.** The mean level of the 1 kHz above the Nyquist frequency, minus that of the band it mirrors under it, in dB. An image of the content at N - x lands at N + x, so this is an upper bound on the images; the band also holds the quantization floor and the window's leakage, which the figure cannot tell apart.

**Cases that look alike.** The figure names a coincidence of frequency. A lowpass at that frequency in mastering or synthesis, an encoder's lowpass and a converter's read the same, and the analyzer names no history from it.

**Observations.** In Encspect's test files only; the analyzer does not use them.
- 1 s of pink noise from 44,100 to 48,000 Hz at 16 bits: found at 44,100 Hz for SoX 14.4.2 `rate -h`, `-v` and `-v -s` (steps of 52.7 to 64.4 dB) and r8brain-free-src 5792291 at transition bands of 0.5, 2 and 4 % (55.6 to 65.8 dB); at 24 bits 87.9 to 100.7 dB. Not found for SoX `-l` and `-q`, or for FFmpeg 6.1.1's default resampler (six files). r8brain at 10 % is found at the window's lowest boundary (38.4 dB at 21,422 Hz).
- Also found for a sweep, synthetic music, and from 22,050, 32,000 and 8,000 Hz; not for the same sources made at 48,000 and 96,000 Hz without conversion.
- Look-alikes: lines that end at 22,050 Hz in a 48,000 Hz file; LAME 3.100 `-m j -b 64` decoded at 24,000 Hz (11,025 Hz); FFmpeg's wmav2 at 128 kbit/s and 48,000 Hz (44,100 Hz); oggenc 1.4.2 `-q 0` at 48,000 Hz (32,000 Hz).

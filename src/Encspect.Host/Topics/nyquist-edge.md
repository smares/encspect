---
id: nyquist-edge
title: Nyquist edge
related: spectral-cutoff, spectral-bandwidth, spectral-slope, lowpass, spectrogram, measurements
references: Hydrogenaudio, Sample Rate Conversion Comparison; SoX 14.4.2, sox --help-effect rate; r8brain-free-src, CDSPResampler.h; Encspect design, docs/design/spectral-analysis.md
---
**The problem.** A converter that raises the sample rate lowpasses at the old rate's Nyquist frequency, half the old rate: audio taken from 44,100 to 48,000 Hz ends in a steep edge near 22,050 Hz.

**How it works.** For each common rate under the stream's own (the PCM and MPEG audio rates from 8,000 to 192,000 Hz) the analyzer takes the cutoff's step (the mean level of 1 kHz of rows under a row boundary against the 1 kHz over it) at the boundaries from 3 % under that rate's Nyquist frequency up to the first boundary over it. An edge is found where the largest step is 25 dB or more, is a local maximum, is followed by a fall of at most a quarter of the step over the next 1 kHz, and shows in at least half of the frames with signal. Several edges are listed, highest first, from the combined spectrum only. Gentle converters leave a slope (see the [spectral slope](spectral-slope)).

**The level above.** The mean level of the 1 kHz above the Nyquist frequency minus that of the band it mirrors under it. An image of the content at N - x lands at N + x, so this bounds the images from above; the band also holds the quantization floor and the window's leakage.

**Cases that look alike.** The figure names a coincidence of frequency. A lowpass at that frequency in mastering or synthesis, an encoder's lowpass and a converter's read the same, and the analyzer names no history from it.

**Observations.** In Encspect's test files only; the analyzer does not use them. One second of pink noise from 44,100 to 48,000 Hz at 16 bits is found at 44,100 Hz for SoX 14.4.2 `rate -h`, `-v` and `-v -s` and r8brain-free-src at transition bands of 0.5, 2 and 4 % (steps of 52.7 to 65.8 dB), and at 24 bits; r8brain at 10 % is found too, at the window's lowest boundary. It is not found for SoX `-l` and `-q` or FFmpeg 6.1.1's default resampler. The same sources made at 48,000 and 96,000 Hz without conversion are not found. Look-alikes are found too: LAME 3.100 `-m j -b 64` at 24,000 Hz, FFmpeg 6.1.1's wmav2 at 128 kbit/s and oggenc 1.4.2 `-q 0` at 48,000 Hz.

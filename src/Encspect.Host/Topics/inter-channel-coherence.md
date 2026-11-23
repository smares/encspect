---
id: inter-channel-coherence
title: Inter-channel coherence
related: intensity-stereo, mid-side-spectra, joint-stereo, spectral-cutoff, measurements
references: P. D. Welch (1967), IEEE Trans. Audio and Electroacoustics 15(2); Carter, Knapp and Nuttall (1973), IEEE Trans. Audio and Electroacoustics 21(4); ISO/IEC 11172-3 2.4.2.3; Encspect design, docs/design/spectral-analysis.md
---
**The problem.** Intensity stereo codes the high frequencies once and rebuilds both channels from that signal, each with its own gain. The decoded channels then hold one signal above some frequency, apart from a gain; how well one channel predicts the other shows where that starts.

**How it works.** The magnitude-squared coherence at a frequency is the squared magnitude of the cross-spectrum of the two channels divided by the product of their power spectra, averaged over the frames. It runs from 0 % to 100 % and is 100 % where one channel is the other through a fixed filter, such as a gain. The analyzer uses the front left and right channels (or the two channels of a stream without a layout) and bands of 1 kHz. It walks down from the end of the content while the band stays at 85 % or more, and reports where it stops, if the band under it is less coherent. It needs 10 frames.

**What lowers it.** A gain that changes inside a frame (85 to 170 ms), a sign that flips, a delay between the channels.

**What raises it.** Intensity stereo, a side signal that mid/side coding quantized to nothing, and mono or nearly mono content. The figure cannot tell them apart, and the analyzer names no encoder from it. A parser's stated intensity stereo bound stands beside the start as a figure, not a finding.

**Observations.** In Encspect's test files: 2 s of stereo pink noise with uncorrelated channels, encoded and decoded again. Starts: TwoLAME 0.4.0 -m j 2,756 Hz at -b 128 and 11,025 Hz at -b 192; opusenc 0.2 (libopus 1.4) 9,422 Hz at 24 kbps and 15,563 Hz at 48 kbps; oggenc 1.4.2 -q 0 5,168 Hz. None for TwoLAME -m s, LAME 3.100 -m s and -m j -b 128 (11,456 Hz with the side 12 dB under the mid) and FFmpeg 6.1.1's aac at 64 kbps. A source with the side 12 dB under reads 79 to 81 % in every band. In two tompg 3.0 streams with a stated bound, the start was 2,347 Hz (stated median 3,216 Hz) and 4,307 Hz (2,833 Hz). One file per setting: nothing follows for other encoders, and the analyzer does not use these.

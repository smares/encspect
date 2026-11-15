---
id: spectral-holes
title: Spectral holes
related: spectral-cutoff, spectral-bandwidth, spectrogram, measurements, mid-side-spectra, noise-shaping
references: Encspect design, docs/design/spectral-analysis.md
---
**The problem.** A lossy coder with too few bits quantizes some bands to zero. Decoded and stored losslessly, such a band reads at the quantization floor between bands that have content: a hole.

**How it works.** A row is a hole in a frame where its power is at most a thousandth (30 dB) of the mean power of the 1 kHz of rows under it and of the 1 kHz over it, up to 99 % of half the sample rate, so the floor above a lowpass is not a hole. The cells are the rows of the frames with signal at that row, as for the [spectral cutoff](spectral-cutoff), from 2 kHz up to the cutoff's edge, else to where the [spectral bandwidth](spectral-bandwidth) floor starts, else to 99 % of half the sample rate. The figure is the share of those cells that are holes, for the combined, mid and side spectra ([mid/side spectra](mid-side-spectra)).

**Limits.** A frame lasts 85 to 170 ms, longer than the unit a coder quantizes in (576 samples in MPEG audio Layer III, 1,024 in AAC LC), so a band zeroed in part of a frame is filled by the rest. The window's main lobe reaches 2 bins: in lines at every bin with bands left out, the rows next to content were never counted, so a band of 2 rows (86 Hz at 44.1 kHz) has none. The gaps between a pure tone's harmonics are holes by this definition (harmonics of 440 Hz read 72 % to 20 kHz); a real instrument's gaps hold noise, whose depth is not measured here.

**Observations.** In Encspect's test files, 1 s of mono pink noise read 0.2 to 0.8 % through LAME 3.100 at -b 32, -b 64 and -V 5, and 0.0 % through its other settings, oggenc 1.4.2, FFmpeg 6.1.1's aac and opusenc. In 2 s of stereo pink noise with a side signal 12 dB under the mid, the side spectrum read 12.2 % for LAME -m j -b 64, 3.2 % at -b 128, 0.9 % for oggenc -q 0, 0.7 % for aac at 64 kbps and none for opusenc; mid spectra read 0.1 % or less and the side of LAME -m s 0.0 %. One file of each setting; the analyzer does not use them to name an encoder.

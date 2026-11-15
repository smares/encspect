---
id: mid-side-spectra
title: Mid/side spectra
related: mid-side-stereo, joint-stereo, spectral-cutoff, spectral-holes, spectrogram, measurements
references: Encspect design, docs/design/spectral-analysis.md
---
**The problem.** A joint stereo coder may code the mid and the side signal with different precision. The decoded left and right channels do not show how the two were coded; their sum and difference do.

**How it works.** For each frame the analyzer takes the complex bins of the transforms of the two channels of the pair, and forms the mid bin as (left + right) / 2 and the side bin as (left - right) / 2. No further transform is needed, and the halving is exact. The mid and side rows are averaged like a channel's, each with a [spectral cutoff](spectral-cutoff) from the frames of its own and [spectral holes](spectral-holes). The pair is the channels the layout names front left and front right, or the two channels of a stream without a layout; other pairs, such as surround pairs, are not measured. Mid and side are views of the pair, not channels: the combined spectrum, the grids and the levels do not include them. Identical channels have a side without power, opposite channels a mid without power.

**What you see.** In the Spectrum graph, a Mid/Side form draws the two averages, each with its cutoff. The Figures list has a Mid/Side line.

**Observations.** In Encspect's test files, 2 s of stereo pink noise with a pink side signal 12 dB under the mid, encoded and decoded again: the side cutoff lay under the mid's for LAME 3.100 -m j by 141 Hz at -b 64 (10,875 against 11,016 Hz) and 388 Hz at -b 128 (16,451 against 16,839 Hz), and for opusenc at 48 kbit/s by 187 Hz. LAME -m s -b 128 and oggenc 1.4.2 -q 0 differed by 43 and 44 Hz, FFmpeg 6.1.1's aac at 64 kbps not at all. Holes in the side spectrum are in [spectral holes](spectral-holes). One file of each setting: no other encoder or setting is inferred from them, and the analyzer does not use them to name an encoder.

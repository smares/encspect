---
id: mid-side-spectra
title: Mid/side spectra
related: mid-side-stereo, joint-stereo, spectral-cutoff, spectral-holes, spectrogram, measurements
references: Encspect design, docs/design/spectral-analysis.md
---
**The problem.** A joint stereo coder may code the mid and the side signal with different precision. The decoded left and right channels do not show how the two were coded; their sum and difference do.

**How it works.** For each frame the analyzer takes the complex bins of the transforms of the two channels of the pair, and forms the mid bin as (left + right) / 2 and the side bin as (left - right) / 2. No further transform is needed. The mid and side rows are averaged like a channel's, each with a [spectral cutoff](spectral-cutoff) from the frames of its own and [spectral holes](spectral-holes). The pair is the channels the layout names front left and front right, or the two channels of a stream without a layout; other pairs, such as surround pairs, are not measured. Mid and side are views of the pair, not channels: the combined spectrum, the grids and the levels do not include them. Identical channels have a side without power, opposite channels a mid without power.

**What you see.** In the Spectrum graph, a Mid/Side form draws the two averages, each with its cutoff. The Figures list has a Mid/Side line.

**Observations.** In Encspect's test files, 2 s of stereo pink noise encoded and decoded again, in two sets: a pink side signal 12 dB under the mid, and one as loud as the mid. With the side 12 dB under, the side cutoff lay under the mid's for LAME 3.100 -m j by 141 Hz at -b 64 and 388 Hz at -b 128, and for opusenc 0.2 (libopus 1.4) at 48 kbps by 187 Hz. With the side as loud, LAME -m j had the same cutoff in both spectra (10,992 and 16,839 Hz), as did LAME -m s (with the side 12 dB under, 43 Hz, one row, apart) and FFmpeg 6.1.1's aac at 64 kbps (at both levels), and opusenc's side had no cutoff found. oggenc 1.4.2 -q 0 stayed 44 Hz under at both levels. So a side cutoff under the mid's followed the side's level for LAME and opusenc. One file of each setting and level: no other encoder or setting is inferred, and the analyzer does not use them to name an encoder. Holes: [spectral holes](spectral-holes).

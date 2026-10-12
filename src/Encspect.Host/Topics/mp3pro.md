---
id: mp3pro
title: mp3PRO and spectral band replication
related: ancillary-data, mpeg-versions, mp3-surround, encoder-detection, sbr
references: mp3guessenc (mp3PRO detection); no public specification
---

**The problem.** At low bitrates there are too few bits to code the high frequencies, so encoders cut them off, and the result sounds muffled.

**How it works.** Spectral band replication (SBR) codes only the lower half of the spectrum in full and rebuilds the upper half in the decoder from the lower half, guided by a small amount of side data: how loud the high frequencies are in each band and over time. High frequencies are noise-like enough that a reconstruction with the right envelope sounds close to the original. mp3PRO, introduced by Thomson and Coding Technologies around 2001, combined an MP3 core at half the sample rate, an MPEG-2 or 2.5 stream, with SBR data hidden in its [ancillary data](ancillary-data). The same technique later became the "HE" in [HE-AAC](sbr).

**Compatibility.** An ordinary MP3 decoder plays the core and skips the SBR data, at the core's sample rate, without the high frequencies. Only an mp3PRO decoder restores them, at twice the core's rate.

**What you see.** No specification of the SBR data is public, so the analyzer recognizes mp3PRO as mp3guessenc does, by a signature in the first bytes of every frame's ancillary data, shows an Enhancement node with the bytes and their data rate, and names an mp3PRO encoder with medium confidence. What the extension data holds is not read.

---
id: lowpass
title: Lowpass filtering
related: ath, lame-tag, bitrate-modes, spectral-cutoff
references: LAME documentation; LAME tag specification (Gabriel Bouvigne)
---

**The problem.** High frequencies cost many bits to code, and at a limited bitrate those bits are missing elsewhere. At the same time, hearing is least sensitive at the top of the range, and many adults hear little above 15 to 17 kHz.

**How encoders handle it.** Most encoders filter out everything above a lowpass frequency before encoding, a lowpass filter. The lower the bitrate, the lower the lowpass frequency they choose, so the bits go to the range where they matter most. LAME picks its lowpass frequency from the bitrate or quality setting unless told otherwise, and many encoders do the same in their own way.

**What you see.** LAME records its lowpass frequency in the LAME tag's Lowpass field, in steps of 100 Hz. That is the setting, a rounded figure the encoder states. When the analysis decodes the audio (the command line's --measure), the analyzer also measures where the spectrum of the decoded audio ends, the [spectral cutoff](spectral-cutoff): an edge above which the spectrum is nearly empty. It may lie near the stated lowpass frequency or not, and it does not need a tag: it comes from the samples, whatever the file has been through since the encoder.

**Coded bandwidth.** For Layer I and II files, from the bit allocation, for AAC files, from max_sfb, and for AC-3 and E-AC-3 files, from chbwcod, the coupling range and spectral extension, the analyzer also shows where the coded data of each frame reaches, without decoding. That is a third figure, not the lowpass frequency: see [coded bandwidth](coded-bandwidth).

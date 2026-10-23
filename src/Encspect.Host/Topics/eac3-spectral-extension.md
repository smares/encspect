---
id: eac3-spectral-extension
title: Spectral extension in E-AC-3
related: ac3-coupling, sbr, eac3-substreams
references: ATSC A/52:2018 Annex E 2.3.3.1 to 2.3.3.13, 3.3, 3.6
---

**The problem.** At low bitrates, coding a channel's full bandwidth leaves too few bits for the frequencies that matter most.

**How it works.** With spectral extension, a channel codes its coefficients only up to a begin frequency. Above it, the decoder copies coefficients from a lower range, from the copy start frequency, into the extension bands, scales each band by a spectral extension coordinate sent per channel, and blends in noise as spxblnd says. The result has the right levels in each band, though not the original waveform. This is the same idea as [SBR](sbr) in AAC, done on the transform coefficients of E-AC-3 rather than with a separate filter bank.

**Its effect on the bit stream.** A channel in spectral extension ends its coefficients where the extension begins, so its exponents and mantissas stop there, and coupling, where also in use, ends there too. The number of rematrixing bands follows from it in 2/0.

**What you see.** In each block that uses it, the channels in spectral extension, the copy start, begin and end codes, the band structure and each channel's coordinates; the statistics count the blocks with spectral extension, and the graph Blocks With Spectral Extension shows them under Coded Bandwidth, since the band above its begin frequency is rebuilt, not coded.

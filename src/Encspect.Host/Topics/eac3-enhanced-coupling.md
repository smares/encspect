---
id: eac3-enhanced-coupling
title: Enhanced coupling in E-AC-3
related: ac3-coupling, eac3-spectral-extension, ac3-rematrixing
references: ATSC A/52:2018 Annex E 2.3.3.14 to 2.3.3.26, 3.3.2, 3.5
---

**The problem.** Standard coupling begins no lower than about 3.4 kHz at 48 kHz and gives each channel only a level per band. At low bitrates an encoder wants to couple lower, and to keep the stereo image, which also depends on the phase between channels.

**How it works.** Enhanced coupling codes the coupling channel once, as standard coupling does, from a begin sub-band as low as the first of 22 sub-bands of 6 or 12 coefficients, 1.17 kHz at 48 kHz. Each coupled channel then gets, for every enhanced coupling band, an amplitude, an angle and a chaos value. The amplitude scales the coupling channel in steps of about 1.5 dB. The angle rotates its phase, in steps of 2 pi / 64. The chaos value says how much random phase the decoder adds, so that the channels are less alike; a transient flag asks for new random values in every block. The first coupled channel has no angle or chaos.

**Why the decoder needs the neighbors.** A phase rotation needs a signal without time domain aliasing, so the decoder rebuilds the coupling channel from the previous, current and next blocks before it applies the angles.

**What you see.** The enhanced coupling flag in the blocks that use it, the range and band structure, and each channel's amplitudes, angles and chaos values, sent in the first block and where they change. The number of rematrixing bands follows the begin code (A/52 Annex E 3.3.2).

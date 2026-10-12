---
id: noise-shaping
title: Noise shaping in LAME
related: scalefactors, global-gain, ath, lame-tag
references: ISO/IEC 11172-3 Annex C (encoder); LAME tag specification (Gabriel Bouvigne)
---

**The problem.** Quantization adds noise to every frequency band. Whether that noise is heard depends on the band: the psychoacoustic model says, for each band, how much noise the music there masks.

**How it works.** A Layer III encoder searches in two loops. The inner loop finds the [global gain](global-gain) at which the granule fits its bits. The outer loop checks the noise in each band against what the model allows and, where there is too much, raises that band's [scalefactor](scalefactors) so it is quantized more finely, then lets the inner loop fit the bits again. Distributing the noise over the bands so that it follows the masking threshold is what LAME calls noise shaping; its options decide how the bands are amplified and when the search stops.

**What you see.** The LAME tag records the noise shaping mode as a number from 0 to 3, set by the quality setting and the preset, and the --nspsytune flag of older versions. The effect shows indirectly in the side information, for example in how often granules use scalefac_scale.

Noise shaping here is not the noise shaping of dithering, which moves the noise of a reduced bit depth toward less audible frequencies.

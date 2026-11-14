---
id: eac3-adaptive-hybrid-transform
title: The adaptive hybrid transform in E-AC-3
related: ac3-mantissas, ac3-audio-blocks, ac3-coupling
references: ATSC A/52:2018 Annex E 2.3.2.16 to 2.3.2.18, 2.3.3.31 to 2.3.3.40, 3.4
---

**The problem.** An audio block of AC-3 has 256 coefficients per channel, which resolves a steady tone poorly: the same tone shows up in every block, and each block pays for it again.

**How it works.** With the adaptive hybrid transform, an encoder takes the six blocks of a frame and applies a second transform, a DCT of six points, across the six coefficients of each bin. For steady signals the energy then gathers in the first few results, and the effective transform covers 1,536 samples. The decoder undoes the DCT before it applies the exponents. A channel may use it only where its exponents are sent once in the frame, so its six blocks share them.

**Its quantizers.** The bit allocation of AC-3 gives each bin a pointer; here the pointer, called hebap, says how the six values of a bin are coded. For hebap 1 to 7 one index selects a six-value vector from a table of the specification (vector quantization). For hebap 8 to 19 each value is a code of 3 to 16 bits (gain adaptive quantization): the small values of a bin may be coded with fewer bits after a gain, and a code that is the quantizer's most negative value, a tag, says that a large value follows it.

**What you see.** A flag per channel for each frame says whether it uses the transform. All six blocks' mantissas of such a channel sit in block 0, preceded by a gain adaptive quantization mode and the gains, and the later blocks have no mantissa bits for it. The statistics count the mantissa bits as for any other channel.

---
id: line-spectral-pairs
title: Line spectral pairs
related: linear-prediction, speex-modes, speex-excitation
references: Speex manual, section 2.1; libspeex 1.2.1 (lsp.c, quant_lsp.c)
---

**The problem.** The coefficients of a linear predictor are fragile: a small error in one can make the synthesis filter unstable. Codecs that send the filter every few milliseconds want a form that quantizes well and cannot go unstable by rounding.

**How it works.** The filter's polynomial is split into a symmetric and an antisymmetric one, whose roots lie on the unit circle and alternate. Their angles are the line spectral pairs (LSPs), ten in Speex's narrowband layer and eight in each higher layer. A filter is stable as long as the angles stay in ascending order, so the decoder only keeps them a margin apart. LSPs that are close together are a sharp resonance of the filter, and they interpolate smoothly from frame to frame.

**In Speex.** A narrowband frame holds its ten LSPs as the sum of an even spacing and the entries of three or five codebooks of 64 vectors (three at the low rates, five at the high). A wideband or ultra-wideband layer holds eight in two stages. The decoder interpolates the last frame's and this frame's LSPs for each subframe and turns them into filter coefficients.

**What you see.** The frame's submode says whether it carries three or five codebook indexes; the report counts the bits, and the decoder uses the indexes.

---
id: ac3-mantissas
title: AC-3 mantissas, grouping and dither
related: ac3-audio-blocks, block-switching, filter-bank
references: ATSC A/52:2018 5.4.3.61 to 5.4.3.63, 7.3, 7.9
---

**The problem.** Each coefficient of a block is sent as an exponent and a mantissa. The bit allocation says how many bits each mantissa gets, and most of the coefficients of a normal block get very few, so the code has to give each coefficient only the bits it needs, and still keep the high bins from dropping out as silence.

**How it works.** A bit allocation pointer (bap) from 0 to 15 picks the quantizer. Pointers 6 to 15 are two's complement fractions of 5 to 16 bits. Pointers 1 to 5 are symmetric quantizers of 3, 5, 7, 11 and 15 levels. The 3, 5 and 11 level ones are grouped: three 3-level values share one 5-bit code, three 5-level values 7 bits, two 11-level values 7 bits, so a value costs 1.67, 2.33 and 3.5 bits. A group is filled in the order the bins come and can be shared across channels; only the first value of a group has bits at its place in the stream. A code that no quantizer has, such as 27 in a 5-bit group or 7 in a 3-bit one, breaks the specification. The decoder shifts each mantissa right by its exponent to get the coefficient.

**Dither.** Where a bin has no bits (bap 0) the decoder fills it with noise instead of zero when the channel's dither flag is set, so that a band that the encoder gave nothing does not drop out. The generator is the decoder's own: A/52 7.3.4 asks for noise and names a uniform distribution scaled by 0.707 as the optimum, with 0.75 or 0.5 acceptable. Two decoders' output differs in those bins from sample to sample, though not in energy.

**What you see.** The Mantissas field of each block holds the codes, which the analyzer reads in every analysis; its length is what the bit allocation gives. The flag IsDithered is listed per channel and block. What the codes sound like is for the decoder: with `--measure` Encspect reconstructs the samples ([block switching](block-switching) and the [filter bank](filter-bank) are the transform that follows).

---
id: ac3-audio-blocks
title: AC-3 audio blocks and bit allocation
related: ac3-frames-and-bsi, ac3-coupling, ac3-rematrixing, block-switching, filter-bank
references: ATSC A/52:2018 5.3.3, 5.4.3, 7.1 to 7.3, 7.9, Annex E 2.2.3 and 2.2.4
---

**The problem.** A transform coder must spend its bits where the ear notices errors and save them where it does not, and the decoder must know exactly how many bits each value takes.

**Block switching.** Each audio block holds 256 new samples per channel, transformed with a window of 512. Where a transient would smear quantization noise before it, the encoder sets blksw and splits the block into two transforms of 256.

**Exponents.** Each coefficient is sent as an exponent and a mantissa. Exponents are coded as differences, one per coefficient (D15) or shared by two (D25) or four (D45), and a block may reuse the previous block's.

**Bit allocation.** The mantissas' sizes are not sent. Encoder and decoder both run the same parametric model of hearing over the exponents: a power spectrum, its masking curve built from decay and gain parameters and a hearing threshold, moved up or down by SNR offsets and optional delta bit allocation. Where the spectrum stands above the curve, mantissas get bits, from 3-level values grouped three to 5 bits up to 16-bit values. The encoder tunes the offsets until the frame is full.

**What you see.** Every field of each block before its mantissas, with each channel's exponent strategy and bit allocation parameters. The analyzer runs the bit allocation itself to know where the mantissas end and the next block starts, as a decoder must; it does not decode them. E-AC-3 moves strategies into the audio frame fields, and its adaptive hybrid transform and enhanced coupling code mantissas the analyzer does not step over, so a frame that uses them is read up to there.

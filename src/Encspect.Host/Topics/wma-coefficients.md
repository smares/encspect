---
id: wma-coefficients
title: Windows Media Audio Professional coefficient codes
related: wma-pro, wma-scale-factors, huffman-coding
references: Encspect's format description docs/design/wma-pro-format.md
---

**The problem.** After quantization the spectrum is mostly small values and runs of zeros. Windows Media Audio Professional codes it with Huffman codes of two kinds, chosen for what the data looks like.

**How a channel's coefficients are coded.** A bit picks one of two codebooks. The coefficients are then coded in groups of four as vectors, in the low part of the spectrum where values are dense: a code for the four values, an escape to two codes for pairs, and an escape again to two codes for single values, which can be followed by a large value of 8, 16, 24 or 31 bits. One sign bit follows for each value that is not 0. The vectors end after a group in which a run of zeros got longer than the subframe length divided by 256 (unless the subframe states how many coefficients are vectors).

**Run-level codes.** The rest of the spectrum is coded as pairs of a run of zeros and a level, each with a sign bit, until an end code. An escape gives a large value and a run of its own. The end code may be left out when the codes reach the end of the spectrum.

**What you see in a file.** Encspect shows, for each channel of a subframe, how many coefficients are not 0, how many run-level codes are escape codes, and which codebook is used, and sums them. In Encspect's test files a code that breaks the syntax does not occur.

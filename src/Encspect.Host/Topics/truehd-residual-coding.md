---
id: truehd-residual-coding
title: TrueHD residual coding
related: truehd-decoding-parameters, truehd-prediction-filters, huffman-coding, rice-coding
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), sections "Block data" and "Huffman codebooks"
---

**The problem.** After prediction, what is left of a sample is small and nearly random, and a few bits can say it if the coder adapts to how large the values are.

**How it works.** For each sample period a block stores one residual per coded channel. Each channel states a codebook (0 to 3), a count of least significant bits and a quantizer step. The bits below the step are not coded, and the rest of the least significant bits are stored as they are. With codebook 0 the residual is only those raw bits, as an offset binary number. With codebooks 1 to 3 a [Huffman](huffman-coding) codeword of up to 9 bits gives the part above the raw bits; the three codebooks differ in the codewords that start with 1, which carry four values in codebook 1, two in codebook 2 and only 0 in codebook 3. A per-channel offset then moves the value. A matrix may also bypass one least significant bit, stored as a bit of its own before the residuals of the sample period.

**What you see.** The codebook, the number of least significant bits and the Huffman offset of each channel under the block's parameters. A bit string that is no codeword, one of the two 9-bit strings no codebook uses, is a finding, as is a block that runs past its substream.

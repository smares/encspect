---
id: wavpack-decorrelation
title: WavPack decorrelation and entropy coding
related: wavpack-blocks, wavpack-sub-blocks, mid-side-stereo, huffman-coding
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020), section 3.0; libwavpack 5.6.0, src/unpack.c, src/decorr_utils.c, src/read_words.c, src/decorr_tables.h
---

**The problem.** To code audio losslessly, an encoder must predict each sample from the ones before it, so that what is left to code is small. Different music needs different predictors.

**How it works.** WavPack runs the samples through a cascade of decorrelation passes. Each pass subtracts a prediction made with an adaptive weight. A term of 1 to 8 predicts from the sample that many places back in the same channel; 17 and 18 extrapolate from the last two samples; negative terms predict a channel from the other one. A delta sets how fast the weight adapts. A stereo block may first be coded as [mid and side](mid-side-stereo). The residual that remains is entropy coded with medians that adapt as it goes. The block stores the starting state: the terms, the weights, the first history samples and the medians.

**What you see.** Decorrelation Terms lists the passes of each block with their terms and deltas, followed by their weights, samples and the entropy variables. libwavpack 5.6.0 writes 2 passes in fast mode, 5 by default, 10 in high mode and 16 in very high mode; extra processing (-x) tries more filters and may use fewer passes. The statistics give the range of passes and the share of stereo blocks coded as mid and side. The audio bitstream itself is not decoded.

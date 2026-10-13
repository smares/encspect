---
id: wavpack-hybrid-mode
title: WavPack hybrid mode and correction files
related: wavpack-blocks, wavpack-decorrelation, bitrate-modes, ape-tag
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020), sections 1.0 and 3.0; libwavpack 5.6.0, src/write_words.c (word_set_bitrate), src/pack_utils.c, src/open_utils.c
---

**The problem.** Lossless files are large, and a lossy file cannot give the original back. Hybrid mode makes one coding that can be either.

**How it works.** The encoder predicts each sample as in lossless mode, but quantizes the residual so that the block takes about a target number of bits. What the quantization leaves out is the error. With a correction file (the -c option), the encoder writes the error into a second file, .wvc, whose blocks match the main .wv file's block for block. The .wv file plays alone, as a lossy file; a decoder given both rebuilds the original exactly. Noise shaping moves the quantization noise in frequency, and the hybrid profile sub-block of each block keeps the state the bitrate control starts from; the target in bits per sample follows from its bitrate accumulator.

**What you see.** IsHybrid in the blocks' flags and a Hybrid Profile with the target bitrate. A .wvc file is read on its own: its blocks hold a correction bitstream and shaping weights, and a finding says it cannot be decoded alone. The analyzer does not look for the .wvc beside a .wv file; the encoder's configuration block says whether it wrote one (CreatesCorrectionFile), which the statistics report as HasCorrectionFile.

---
id: wma-frames-and-blocks
title: Frames and blocks in Windows Media Audio
related: wma-standard, mid-side-stereo, noise-substitution, huffman-coding
references: FFmpeg 6.1.1 libavcodec/wmadec.c (wma_decode_frame, wma_decode_block), wma.c (ff_wma_init, ff_wma_run_level_decode), wmadata.h
---

**The problem.** One transform size does not suit all sound. Long blocks resolve steady tones well; at an attack they spread the error before it. Short blocks do the reverse.

**How it works.** A frame holds frame-length samples per channel and is divided into one or more blocks. A stream with variable block lengths has a few block sizes, each half the one before it, and every block states the size of the one that follows. A block has: for stereo, a bit that codes the channels as mid and side; a flag per channel that says whether it is coded; a total gain; at low bitrates, flags for high bands that are replaced by noise, and the noise levels; the exponents; and the coefficients. Coefficients are run-length coded: a Huffman code stands for a run of zeros and a level, a sign bit follows, and codes for the end of the block and for an escape come first. Which of the three pairs of codebooks a stream uses follows from its sample rate and its bits per sample of audio, the bitrate over the channels and the rate.

**What you see.** Each block with its fields at their bits. The statistics count blocks of each size and the blocks with mid/side stereo and noise substitution, and the non-zero and escape-coded coefficients.

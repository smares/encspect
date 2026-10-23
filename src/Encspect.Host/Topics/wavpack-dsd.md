---
id: wavpack-dsd
title: DSD audio in WavPack
related: wavpack-blocks, wavpack-sub-blocks, wavpack-wrapper, wavpack-audio-checksums
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020), section 2.0 (bit 31); libwavpack 5.6.0, src/unpack_dsd.c, src/pack_dsd.c, src/common_utils.c
---

**The problem.** Direct Stream Digital stores audio as one bit per sample at a rate such as 2,822,400 samples a second, 64 times that of a CD, with no PCM in between. Its files are large, and DSD cannot be fed to the usual predictors.

**How it works.** WavPack 5 compresses DSD losslessly. It treats eight bits as one byte-sized sample, so a block counts bytes. The flags mark the block as DSD, and the sample rate field gives the byte rate divided by a power of two, which a DSD block sub-block restores: the audio's bit rate is the stored rate times that factor times 8. The sub-block holds the coding mode: the bytes stored as they are, or coded with a probability model and range coding in a fast or a high mode, the fast mode looking at up to five previous bits. Hybrid mode is not available for DSD.

**What you see.** IsDsd in the flags and statistics, and a DSD Block with its rate multiplier, mode and history bits. The statistics give the sample rate as the DSD bit rate and the samples as bits, so the duration is right; the bitstream is decoded when you ask the analyzer to verify the audio ([checks over the decoded audio](wavpack-audio-checksums)). A source file of DSF or DSDIFF format shows in the new config block.

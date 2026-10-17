---
id: wavpack-sub-blocks
title: WavPack sub-blocks
related: wavpack-blocks, wavpack-decorrelation, wavpack-hybrid-mode, wavpack-wrapper, crc
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020), section 3.0; libwavpack 5.6.0, src/open_utils.c (read_metadata_buff, process_metadata)
---

**The problem.** A block must carry the settings its decoder starts from, the audio itself and, optionally, extras such as the header of the original file. A newer encoder must be able to add data that older decoders can skip.

**How it works.** After the header, a block holds metadata sub-blocks. Each has an ID byte, a size of one byte, or three for a large sub-block, counted in words of two bytes, and its data, padded to an even length. In the ID, the low six bits say what the sub-block holds, and bit 5 (0x20) among them marks one a decoder may skip if it does not know it. Bit 6 (0x40) says the data is one byte shorter than the size, and 0x80 that the size takes three bytes.

The decoding setup comes first: decorrelation terms, weights and samples, entropy variables, the hybrid profile, float and integer details, channel info. The compressed audio follows, normally the last of them, and WavPack 5 adds a checksum of two or four bytes at the end of the block. The optional ones hold the wrapper of the source file, a configuration block, an MD5 digest and a sample rate the header cannot name.

**What you see.** Each sub-block under its name, with its ID flags, its size and its data, decoded where the analyzer reads it. An unknown optional sub-block is an info; libwavpack refuses a block with an unknown sub-block that is not optional, and a sub-block that runs past its block.

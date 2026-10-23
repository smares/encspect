---
id: wavpack-audio-checksums
title: WavPack checks over the decoded audio
related: verification, wavpack-blocks, wavpack-wrapper, wavpack-hybrid-mode, wavpack-float-and-large-integers, crc
references: libwavpack 5.6.0, src/unpack.c (the CRC of each block), src/unpack_floats.c, src/unpack_dsd.c, src/open_utils.c (read_new_config_info, process_metadata), include/wavpack.h (QMODE_*) and cli/wvunpack.c (unpack_audio, unpack_dsd_audio)
---

**The problem.** The checksum of a block's bytes says the bytes are intact. A lossless decoder also wants to know that it rebuilt the audio the encoder read.

**How it works.** Every block header stores a CRC of the block's decoded samples. For one channel it starts at all ones and, for each sample, adds the sample to three times the sum so far; for two channels it adds three times the left sample and the right sample to nine times the sum. The CRC covers the samples before the shift and the restoring of floating point numbers and large integers, which keeps its own CRC over the restored values in the extra bitstream ([float and large integers](wavpack-float-and-large-integers)). The CRC of a DSD block is worked out over its bytes as a mono block's is. The encoder may also store an MD5 of the whole audio, in the byte layout of the file it read: that of a WAVE file (little-endian, 8-bit samples unsigned) unless the new config block's qualify mode says big-endian, signed bytes, unsigned words, channels in the source's order or, for DSD, blocks of 4,096 bytes of each channel, with the bits of each byte reversed where the source stored them least significant bit first. A layout other than the WAVE one is stored in the alternate MD5 sub-block.

**Hybrid files.** A hybrid file without its correction file decodes to the lossy audio, so its block CRCs hold for that audio, but its MD5 is of the lossless original, which only a decoder given the correction file reproduces ([hybrid mode](wavpack-hybrid-mode)).

**What you see.** The CRC in each block's header, the CRC of the extra bitstream and the MD5 sub-block as stored. They can be compared only after decoding, so the analyzer compares them when you ask it to verify the audio ([verification](verification)).

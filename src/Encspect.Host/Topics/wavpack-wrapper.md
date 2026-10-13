---
id: wavpack-wrapper
title: WavPack wrapper, source format and MD5
related: wavpack-sub-blocks, wavpack-dsd, ape-tag
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020), section 3.0; libwavpack 5.6.0, include/wavpack.h (WP_FORMAT_*, QMODE_*) and src/open_utils.c (read_new_config_info)
---

**The problem.** WavPack codes the audio. People also want the file it came from back, byte for byte, with its header and any chunks around the audio.

**How it works.** The encoder keeps the source file's header in the first block and its trailer in the last, as sub-blocks. For a WAVE file these are the RIFF header and trailer, so decoding can write the same file. WavPack 5 added alternate headers and trailers for AIFF, Core Audio (CAF), Sony DSF, DSDIFF and Wave64 files, the extension to give the decoded file, and a new config block: the source's format, flags for how its samples are stored (big-endian, signed 8-bit, unsigned, channels reordered, the order of DSD bits) and a Core Audio channel layout. An MD5 digest of the audio data can be stored, to compare with the decoded audio.

**What you see.** The RIFF Header decoded into its chunks (the format chunk with the rate, channels and bits of the source), the alternate header's signature, Source Format and Source Extension in the statistics, and the MD5 digest as stored. The analyzer does not decode the audio, so it cannot check the digest.

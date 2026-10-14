---
id: wavpack-float-and-large-integers
title: WavPack float and large integer samples
related: wavpack-sub-blocks, wavpack-hybrid-mode, wavpack-decorrelation
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020), sections 2.0 and 3.0; libwavpack 5.6.0, src/pack_floats.c, src/unpack_floats.c, src/pack.c (send_int32_data), src/open_utils.c
---

**The problem.** WavPack's main bitstream codes integers of up to 24 bits. Audio of 32 bits, as integers or as floating point numbers, has more information than that.

**How it works.** For 32-bit floating point numbers, a float info sub-block says what a decoder has to restore: bits shifted into the numbers, zeros that are not all real zeros, negative zeros and exceptions such as infinities, plus the exponents. For integers of more than 24 bits, and integers that need a shift, an int32 info sub-block says how many bits are sent literally and how many are always zero, always one or copies of the bit above. What the main bitstream cannot hold goes into an extra bitstream with a CRC of its own. In a lossless file that stream is in the main file; with a hybrid file, it is in the correction file.

**What you see.** IsFloatingPoint or HasExtendedIntegers in the flags, a Float Info or Int32 Info and an Extra Bitstream with its CRC. Without a correction file, a hybrid file has no extra bitstream and loses those bits; the statistics report IsLossless as false for it, as they do for any hybrid file. The analyzer does not decode the bitstreams or check the CRC.

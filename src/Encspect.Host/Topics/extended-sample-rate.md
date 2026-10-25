---
id: extended-sample-rate
title: The 80-bit extended sample rate
related: aiff-common-chunk, pcm-samples
references: Apple, Audio Interchange File Format AIFF-C (draft of 8/26/91), section 1.0 (data types) and 4.0; IEEE 754
---

**The problem.** The designers of AIFF wanted to store a sample rate of any size, including fractions such as 22254.54 Hz, and chose the number format of the Macintosh's math library.

**How it works.** The extended type has 80 bits in ten bytes. The first bit is the sign. The next 15 bits are the exponent, which is stored with a bias of 16383. The last 64 bits are the mantissa, whose leading bit is written out instead of implied, as it is in the 32 and 64-bit formats of IEEE 754. The value is the mantissa times 2 to the power of the exponent less the bias less 63.

44100 Hz shows how it comes out. As a binary number it is 1010110001000100 followed by zeros, which fills the top of the mantissa: 0xAC44000000000000. The leading bit sits at position 15, so the exponent is 16383 plus 15, 0x400E. The ten bytes are 40 0E AC 44 00 00 00 00 00 00. The exponent field 0x7FFF is reserved for infinity and not-a-number, and no sample rate can use it.

**What you see.** The rate in Hz, with the sign and exponent and the mantissa as hexadecimal below it. A rate that is not a positive finite number is reported as a finding and taken as 0, which leaves the stream without a length.

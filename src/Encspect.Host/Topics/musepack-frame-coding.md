---
id: musepack-frame-coding
title: Musepack: how a frame's data are coded
related: musepack-subband-coding, musepack-sv7-stream, musepack-sv8-packets, noise-substitution, mid-side-stereo, lossy-decoding
references: libmpcdec r495 (mpc_decoder.c, huffman.c, mpc_bits_reader.c, requant.c, synth_filter.c); libmpcdec 1.2.6 (mpc_decoder.c, huffsv46.c)
---

**What a frame holds.** For each of the 32 subbands and each channel a frame states a resolution; then, for every coded subband, three scalefactors (one for each third of the frame's 36 samples) and the 36 quantized samples. SV4 to SV7 start a frame with its length in bits, which the data must take exactly. SV8 frames have no length: each starts where the one before ends, and the packet's size is the check.

**Scalefactors.** A scalefactor is an index into a table that steps by about 1.6 dB. A frame mostly codes it as a difference from the last one of the same subband and channel, in a Huffman code with an escape to 6 bits (SV7), or in 7 bits absolute and then differences (SV8's first frame of a packet, which makes every packet decodable alone). A selection code says which of the three repeat.

**Samples.** The resolution decides the code. Resolutions 1 and 2 pack 3 or 2 values into one Huffman code, 3 to 7 code one value each, in one of two tables the frame picks, and 8 and up store the value in 7 to 16 bits, less an offset. SV8 uses canonical Huffman codes, switches tables by the size of the values just read, codes resolutions 9 and up as a Huffman code for the high bits followed by plain bits, and codes resolution 1 by saying which of 18 samples are not zero with an enumerative code and then their signs. A value times the resolution's coefficient times the scalefactor is the subband sample; resolution -1 codes nothing, and a decoder fills the subband with pseudo-random values.

**Rebuilding the audio.** Where a subband is mid/side coded, left is the sum of mid and side and right their difference. The 36 slots of 32 subband samples then go through the synthesis filterbank, which carries 480 samples over from the frame before.

**What you see.** A frame whose data take other bits than its length states is reported as damaged: libmpcdec stops at it.

---
id: musepack-sv4-to-sv6
title: Musepack SV4 to SV6: the stream versions before SV7
related: musepack-sv7-stream, musepack-subband-coding, mid-side-stereo, intensity-stereo
references: libmpcdec 1.2.6 streaminfo.c, mpc_decoder.c, huffsv46.c; the Portable Musepack decoder (mpc.zip, 2004) streaminfo.cpp, mpc_dec.cpp; Rarewares' Win32 builds of the SV4 and SV5 encoders and of mppdec
---

**The header.** Before "MP+" there was no magic number. The first 32-bit little-endian word, read from its most significant bit like all of the stream, holds the bitrate (0 for a variable bitrate), the intensity stereo and mid/side flags, the stream version, the max band and the block size. The frame count follows in 16 bits in version 4 and 32 bits in versions 5 and 6. The streams are stereo at 44.1 kHz.

**Frames.** Each block of block-size frames starts with a 20-bit length. A frame opens with each subband's resolutions: a Huffman code for the left channel, from one of three tables by subband range, the mid/side flag where the header allows mid/side, then the right channel's code. There is no noise substitution.

**The last frame.** The SV4 and SV5 encoders write whole words only, so their last counted frame can run up to 31 bits past the end of the data; decoders decode the frames before it. In version 6, libmpcdec 1.2.6 and the Portable Musepack decoder decode the last frame too, then read 11 bits that say how many of its samples belong to the audio.

**What decoders take.** Both decoders refuse a constant bitrate, intensity stereo and blocks of more than one frame; mppdec decodes them. No SV6 encoder was found, so the SV6 frame syntax rests on the two decoders' source and on SV5 streams relabeled as version 6.

**What you see.** The header field by field and, where the decoders decode the stream, each frame with its length, used subbands and mid/side subbands.

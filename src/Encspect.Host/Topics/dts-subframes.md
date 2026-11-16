---
id: dts-subframes
title: DTS subframes
related: dts-core-frames, dts-audio-coding-header, dts-transient-mode, subband-coding, huffman-coding, filter-bank, coded-bandwidth
references: ETSI TS 102 114 V1.6.1 (2019-08), 5.5, 5.6, Tables 5-26 to 5-29, Annex C.3.2, Annex D.5 and D.6
---

**The problem.** A DTS core frame holds hundreds of samples for up to five channels and has to say, for each of 32 subbands, how precisely it was coded, and then give the samples in the cheapest way for that precision.

**How it works.** The audio of a frame is split into up to 16 subframes that a decoder reads without the others. Each starts with side information: for each channel, which subbands use subband prediction (PMODE and the 12-bit index of its four coefficients), the bit allocation index ABITS of each subband that is not vector quantized, the transient modes ([transient mode](dts-transient-mode)) and the scale factors, then the joint intensity scale factors and, if the frame sets DYNF, a dynamic range coefficient.

The 10-bit addresses of the vector quantized high-frequency subbands follow, then the decimated samples of the LFE channel as 8-bit values with a scale factor, and then one to four subsubframes of 8 samples for each coded subband.

ABITS selects a mid-tread quantizer of 3 to 8,388,608 levels. The quantization indexes are written as plain two's complement numbers, as [Huffman codes](huffman-coding) from the books of Annex D.5, or, up to 25 levels, as block codes: four samples in one number of base 3 to 25 (C.3.2). The header's SEL for each ABITS picks the form. A subframe ends with the word 0xFFFF.

**What you see.** The subframes are read in every analysis and shown as the core frame's audio data, with the figures they give: the [coded bandwidth](coded-bandwidth) and the subframes with transients. A subframe that does not end with 0xFFFF, uses a table index that is invalid, or runs past its frame is a finding, and the frame's samples are not read.

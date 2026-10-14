---
id: dts-audio-coding-header
title: DTS primary audio coding header
related: dts-core-frames, dts-channel-arrangements, subband-coding, huffman-coding
references: ETSI TS 102 114 V1.6.1 (2019-08), 5.4.3 and Tables 5-21 to 5-27
---

**The problem.** The subframes after the header are Huffman or block coded in tables the encoder picks for each channel, and the decoder needs to know which ones before it reads a bit.

**How it works.** The primary audio coding header lists, for each of up to five channels, how that channel's subbands are coded:

- SUBS: the number of active subbands, from 2 to 32. Samples above them are zero, unless joint intensity coding fills them.
- VQSUB: the first subband whose samples are vector quantized, one 10-bit address per subband and subframe.
- JOINX: whether the channel copies the subbands above its own from an earlier channel by joint intensity coding, and from which.
- THUFF, SHUFF and BHUFF: the codebooks of the transient positions, the scale factors and the bit allocation indexes (ABITS), which select the quantizer of each subband.
- SEL: for each ABITS from 1 to 10, the codebook of the quantization indexes; ADJ then scales the scale factors where SEL names a Huffman code.

The header also carries SUBFS, the number of subframes, and PCHS, the number of primary channels.

**What you see.** The header as nodes with one child for each channel of every field. Values the tables reserve, such as SHUFF 7, a joint source that comes after its channel, or more active subbands than the 32 of the filter bank, are findings. When the frame's CPF flag is set, a CRC follows; the specification says it should not be tested, and the analyzer reports it as not verified.

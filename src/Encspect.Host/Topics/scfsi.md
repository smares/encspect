---
id: scfsi
title: Scalefactor selection information (scfsi)
related: scalefactors, granules-and-frames, block-switching, subband-coding
references: ISO/IEC 11172-3 2.4.1.7, 2.4.2.7
---

**The problem.** An MPEG-1 Layer III frame holds two granules, each with its own [scalefactors](scalefactors) for every channel. Over 13 milliseconds the sound often changes little, so granule 1's scalefactors are often the same as granule 0's, and sending them twice wastes bits.

**How it works.** The side information carries four scfsi bits per channel, one for each group of scalefactor bands: bands 0 to 5, 6 to 10, 11 to 15 and 16 to 20. Where a bit is set, granule 1 sends no scalefactors for that group and the decoder reuses those of granule 0. The bits saved go to the audio itself. The encoder decides group by group, so it can reuse the scalefactors of the steady low bands while sending new ones for high bands that changed.

**Why not with short blocks.** The four groups are groups of the long-block scalefactor bands. A granule with short blocks has scalefactors per window over a different set of bands, and short blocks are used precisely where the sound changes suddenly. ISO/IEC 11172-3 therefore calls for scfsi to be 0 where short blocks are used. minimp3 ignores the bits for a channel with short blocks, libmad rejects such frames, and the analyzer reports them.

**MPEG-2 and 2.5** have one granule per frame, so there is nothing to reuse and no scfsi.

**What you see.** The SCFSI node of the side information shows each channel's four bits, and the statistics say whether any frame uses scfsi. Some encoders never use it, which helps the analyzer tell them apart. Layer II has an scfsi too, with a different meaning: see [subband coding](subband-coding).

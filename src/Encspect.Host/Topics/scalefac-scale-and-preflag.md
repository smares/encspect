---
id: scalefac-scale-and-preflag
title: scalefac_scale and preflag
related: scalefactors, global-gain
references: ISO/IEC 11172-3 2.4.2.7, 2.4.3.4, Annex B
---

Two bits of each granule's side information change how its [scalefactors](scalefactors) are read.

**scalefac_scale** sets the step of a scalefactor. Normally each step changes the level of a band by about 3 dB; with scalefac_scale set, by about 6 dB. The scalefactors have only a few bits each, so the coarser step lets the encoder reach much larger amplifications with the same bits, at the cost of finer control. Encoders set it where a band needs more amplification than the normal range allows. The statistics show the share of granules that set it, as EncSpot's "Scalefac" did, and some encoders never set it.

**preflag** adds fixed amounts, from a preemphasis table of ISO/IEC 11172-3, to the scalefactors of the high bands 11 to 20 of a long block. High bands often need large scalefactors; with the fixed part added by the decoder, the stored scalefactors can be smaller and take fewer bits. MPEG-2 and 2.5 have no preflag bit; their decoders derive it from scalefac_compress.

The analyzer shows scalefac_scale as HasCoarseScalefactors and preflag as HasPreflag.

---
id: celt-folding
title: Folding in CELT
related: opus-celt, replicated-bands, sbr
references: RFC 6716 4.3.4, 5.3.4.3; libopus 1.5.2 celt/bands.c (quant_partition, quant_band)
---
**The problem.** A CELT band with too few bits for even one pulse would decode as silence, a hole in the spectrum. CELT codes each band's energy separately, so the band can still be given the right level; what it lacks is a shape.

**How it works.** RFC 6716 5.3.4.3 says a band without at least half a bit per sample gets no pulses and "folding is used instead". The decoder fills the band with the normalized coefficients of a lower band, the folding source, adds or subtracts 1/256 to each from a pseudo-random sign, renormalizes the vector and scales it by the band's coded energy. The copy is not exact: the small random steps and the renormalization change it. Where there is no source (the lowest bands), or where the frame's spread setting is aggressive on a long block without a time-frequency change, the decoder fills the band with pseudo-random noise instead, or leaves it empty.

**What you see.** The source is chosen for each band and frame by the allocation, so the offset between a filled band and its source can differ from band to band and frame to frame. The decoded spectrum then holds, in the filled bands, the fine structure of lower bands. The [replicated bands](replicated-bands) figure looks for such copies in the decoded audio; it cannot tell folding from [SBR](sbr) or any other copy, and an offset that changes from frame to frame matches too little to be found.

**Where it is specified.** The shape is in RFC 6716 4.3.4 and the allocation in 5.3.4.3; the reference decoder is libopus, whose `quant_partition` in celt/bands.c Encspect's decoder follows.

---
id: scalefactors
title: Scalefactors and scalefactor bands
related: scfsi, scalefac-scale-and-preflag, global-gain, huffman-coding
references: ISO/IEC 11172-3 2.4.2.7, 2.4.3.4; ISO/IEC 13818-3 2.4.3.2
---

**The problem.** Quantizing the frequency lines adds noise. How much noise the ear tolerates differs from band to band, depending on what else is playing at that frequency. A single quantizer for the whole spectrum would put too much noise in some bands and waste bits in others.

**How it works.** Layer III groups the 576 frequency lines of a granule into scalefactor bands, 21 with a scalefactor in a long block and 12 per window in a short one, narrow at low frequencies and wide at high ones, roughly following how the ear groups frequencies. Each band has a scalefactor that amplifies it before quantization. A larger scalefactor makes the quantization relatively finer in that band, so it gets less noise and costs more bits. The [global gain](global-gain) sets the step size for the whole granule; the scalefactors shape the noise band by band.

**How they are stored.** Scalefactors are part of the main data. Their lengths in bits come from scalefac_compress, as slen1 for the low bands and slen2 for the high ones in MPEG-1, or as four lengths over a partition of the bands in MPEG-2 and 2.5. [scfsi](scfsi) lets granule 1 reuse granule 0's scalefactors, and [scalefac_scale and preflag](scalefac-scale-and-preflag) change their step and add fixed amounts.

Layers I and II also have scalefactors, one per subband in steps of 2 dB, which set the range of the subband's samples ([subband coding](subband-coding)).

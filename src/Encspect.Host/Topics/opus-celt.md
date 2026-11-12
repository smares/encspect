---
id: opus-celt
title: Opus CELT: bands, energy and pulses
related: opus-modes, block-switching, intensity-stereo, mid-side-stereo, opus-range-coder, coded-bandwidth
references: RFC 6716, section 4.3
---

**The problem.** CELT codes music with a transform (an MDCT) and must keep the loudness of every part of the spectrum right even at a few bits a band, so that a band never drops out. It splits the spectrum into 21 bands and codes each band's energy apart from its shape.

**Energy and shape.** The energy of each band is coded in steps of 6 dB, predicted from the frame before (or from nothing in an intra frame) and refined by a few extra bits. The shape is a unit vector: a number of pulses spread over the band's coefficients (pyramid vector quantization), whose count the bit allocation sets from the frame's budget. A band that gets no pulses is filled with noise or with a copy of a lower band, so it keeps its energy.

**Transients.** A frame's transient flag turns its one long transform into 2, 4 or 8 short ones, so that a sudden onset spreads less in time (the other formats' [block switching](block-switching)).

**Stereo.** A stereo frame codes the bands below its intensity bound either as mid and side with an angle between them (see [mid/side stereo](mid-side-stereo)) or, if it sets dual stereo, as left and right on their own. From the bound up the bands share one channel and a gain for each channel, [intensity stereo](intensity-stereo).

**Around the transform.** A frame can carry a pitch postfilter, which restores the harmonic structure that the coarse quantization smears, and an anti-collapse bit that tells the decoder to fill the short blocks of a transient frame that had no pulses.

**What you see.** The statistics count the frames that use short blocks and the stereo codings, and the series give the lowest band from which a frame codes intensity stereo.

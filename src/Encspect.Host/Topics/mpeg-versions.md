---
id: mpeg-versions
title: MPEG-1, MPEG-2 and MPEG 2.5
related: mpeg-layers, granules-and-frames, frame-header
references: ISO/IEC 11172-3; ISO/IEC 13818-3
---

**MPEG-1** (ISO/IEC 11172-3, 1993) defined audio coding at 32, 44.1 and 48 kHz, for two channels at most.

**MPEG-2** (ISO/IEC 13818-3) added two things. The first is the lower sample rates, half of MPEG-1's: 16, 22.05 and 24 kHz, with lower bitrates down to 8 kbps, for speech and for streams with little bandwidth. This part is often called LSF, for low sampling frequencies. The second is the [multichannel extension](mpeg-multichannel), which carries up to 5.1 channels in a way MPEG-1 decoders still play as stereo.

**MPEG 2.5** is not a standard. Fraunhofer extended Layer III to a quarter of MPEG-1's rates, 8, 11.025 and 12 kHz, by taking one bit of the sync word as a further version bit. Most decoders play it.

**What changes at the lower rates.** In Layer III a frame of MPEG-2 or 2.5 holds one granule of 576 samples instead of two, so its side information is smaller: an 8-bit main_data_begin instead of 9, no scfsi, and a different way of coding the scalefactor lengths. Layers I and II keep their frame sizes in samples, and Layer II uses its own allocation table.

**What you see.** The Version field of each frame header names the version. MPEG 2.5 Layer I and II frames are reported as a warning, since MPEG 2.5 only extends Layer III.

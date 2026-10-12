---
id: subband-coding
title: Layers I and II: subbands, bit allocation and scalefactors
related: filter-bank, mpeg-layers, intensity-stereo, scfsi
references: ISO/IEC 11172-3 2.4.1.5, 2.4.1.6, 2.4.2.5, 2.4.2.6, Annex B
---

**Subbands.** Layers I and II split the audio with a polyphase filterbank into 32 subbands of equal width ([filterbank](filter-bank)) and code the samples of each subband directly: 12 per subband in a Layer I frame, 36 in Layer II, in three parts of 12.

**Bit allocation.** The encoder's psychoacoustic model decides how precisely each subband must be coded so its noise stays masked. The bit allocation of each frame says it: how many bits each subband's samples get, or none, which leaves the subband out. In Layer I, the allocation is a number of bits per sample; in Layer II it is an index into tables of the standard that depend on the bitrate and the sample rate, with fewer choices in the high subbands, which saves side information.

**Scalefactors.** Each coded subband has a 6-bit scalefactor that scales its samples to the quantizer's range, in steps of 2 dB, so small and large signals get the same relative precision.

**scfsi in Layer II.** A Layer II frame spans three parts, and a subband's level often stays the same across them. Its scfsi, 2 bits per coded subband, says whether three, two or one scalefactor are sent: one for all three parts, or two where the level changes once. This is unlike Layer III's [scfsi](scfsi), which reuses scalefactors between granules.

**Intensity stereo** in joint stereo frames codes the subbands above a bound once, for both channels ([intensity stereo](intensity-stereo)). Whatever follows the samples is [ancillary data](ancillary-data).

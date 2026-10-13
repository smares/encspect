---
id: intensity-stereo
title: Intensity stereo
related: joint-stereo, mid-side-stereo, subband-coding, musepack-sv7-stream
references: ISO/IEC 11172-3 2.4.2.3 (mode_extension), 2.4.3.4 (Layer III decoding); ISO/IEC 13818-3; ISO/IEC 14496-3 4.6.8.2
---

**The problem.** At low bitrates there are not enough bits to code two full channels.

**Why it can work.** Our ears place high-frequency sounds mostly by how loud they are in each ear, following their envelope, rather than by the exact shape of the waveform. Above a few kilohertz, the fine detail that differs between the left and right channels contributes little to where a sound seems to come from.

**How it works.** Intensity stereo codes the high frequencies only once, as a single shared spectrum, and adds for each band a direction: how much of it goes to the left and how much to the right. The decoder rebuilds both channels from the shared signal and the directions. What is lost is the difference between the channels beyond their loudness in those bands, so intensity stereo is an irreversible trade, unlike [mid/side stereo](mid-side-stereo).

**In MPEG audio.** In Layers I and II the Mode Extension field gives a bound: subbands from 4, 8, 12 or 16 upward are coded once with a scalefactor for each channel. In Layer III intensity stereo starts at the first scalefactor band above the highest nonzero frequency line of the right channel, and that channel's scalefactors there carry the direction instead of a level. It can be combined with mid/side stereo for the bands below.

**In AAC.** The second channel of a pair marks intensity bands with codebook 15, in phase, or 14, out of phase, where ms_used inverts the sign, and codes a position instead of a scalefactor (ISO/IEC 14496-3 4.6.8.2).

**What you see.** Mode Extension shows "Intensity stereo" or a band range, and AAC pairs their Intensity Stereo Bands; the statistics count both.

**In Musepack**, SV4 to SV6 headers carry an intensity stereo flag, which libmpcdec 1.2.6 refuses.

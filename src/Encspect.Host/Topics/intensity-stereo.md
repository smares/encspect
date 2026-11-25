---
id: intensity-stereo
title: Intensity stereo
related: joint-stereo, mid-side-stereo, inter-channel-coherence, subband-coding, musepack-sv7-stream
references: ISO/IEC 11172-3 2.4.2.3 (mode_extension), 2.4.3.4.9.3 (Layer III decoding); ISO/IEC 13818-3 2.4.3.2; ISO/IEC 14496-3 4.6.8.2; ETSI TS 102 114 5.4.3, C.3.4 (DTS); ffmpeg libavcodec/aacdec_template.c
---

**The problem.** At low bitrates there are not enough bits to code two full channels. Above a few kilohertz, ears place sounds mostly by how loud they are in each ear, not by the exact waveform, so the fine detail that differs between the channels adds little to where a sound seems to come from.

**How it works.** Intensity stereo codes the high frequencies only once, as a single shared spectrum, and adds for each band a direction: how much of it goes to the left and how much to the right. The decoder rebuilds both channels from the shared signal and the directions. What is lost is the difference beyond loudness in those bands, so it is an irreversible trade, unlike [mid/side stereo](mid-side-stereo).

**In MPEG audio.** In Layers I and II the Mode Extension field gives a bound: subbands from 4, 8, 12 or 16 upward are coded once with a scalefactor for each channel. In Layer III it starts at the first scalefactor band above the highest nonzero line of the right channel (per window in short blocks), whose scalefactors there carry the direction (the intensity position, 0 to 6 in MPEG-1; 7 marks a band not intensity coded). It can combine with mid/side stereo below.

**In AAC.** The second channel of a pair marks intensity bands with codebook 15, in phase, or 14, out of phase, where ms_used inverts the sign (only with ms_mask_present 1, as in faad2 and fdk-aac), and codes a position instead of a scalefactor (ISO/IEC 14496-3 4.6.8.2). Those codebooks count in any pair, as FFmpeg 6.1.1 and faad2 decode them (fdk-aac needs a common window); a pair without one is reported with a warning and counted as intensity stereo.

**What you see.** Mode Extension shows "Intensity stereo" or a band range, and AAC pairs their Intensity Stereo Bands. Intensity Stereo Bound, in MPEG audio, AAC and Opus, gives the lowest frequency, in hertz, from which each frame or unit codes intensity stereo.

**In DTS**, a core channel can copy the subbands above its own active ones from an earlier channel, scaled by JOIN_SCALES (JOINX in the [audio coding header](dts-audio-coding-header)): the copy is by channel, not a direction for each band as above.

**In Musepack**, SV4 to SV6 headers carry an intensity stereo flag, which libmpcdec 1.2.6 refuses.

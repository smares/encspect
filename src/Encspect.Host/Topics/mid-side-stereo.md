---
id: mid-side-stereo
title: Mid/side stereo
related: mid-side-spectra, wma-frames-and-blocks, flac-channel-assignment, wavpack-decorrelation, monkeys-audio-prediction, joint-stereo, intensity-stereo, channel-modes, square-polar-coupling, opus-modes, musepack-subband-coding
references: ISO/IEC 11172-3 2.4.2.3 (mode_extension), 2.4.3.4 (Layer III decoding); ISO/IEC 13818-3; ISO/IEC 14496-3 4.6.8.1; RFC 6716, sections 4.2 and 4.3; RFC 9639 (FLAC), section 9.2; ETSI TS 102 114 C.3.5 (DTS)
---

**The problem.** In most stereo music the left and right channels are much alike. Coding them separately spends bits twice on nearly the same signal.

**How it works.** Mid/side stereo codes the sum and the difference of the channels instead. The mid channel carries what left and right share; the side channel carries only what differs between them. Where the channels are alike, the side channel is small and takes few bits. The decoder turns them back into left and right; the conversion loses nothing. Unlike [intensity stereo](intensity-stereo), nothing of the stereo image is given up.

**Per frame.** In MPEG audio Layer III, mid/side stereo is one of the [joint stereo](joint-stereo) tools, and the encoder decides for every frame whether to use it, through the Mode Extension field of the frame header. Where the channels differ a lot, left/right can cost fewer bits. The Joint Stereo Modes statistics show how often a file uses each choice.

**What you see.** Mode Extension shows "Mid/side stereo" or "Mid/side and intensity stereo" in frames that use it.

**In AAC.** A channel pair with a common window chooses per scalefactor band: ms_mask_present 1 sends an ms_used flag for each band, 2 codes every band as mid/side. Each pair shows its mask and Mid/Side Bands; Stereo Coding counts the pairs with at least one mid/side band, leaving out intensity stereo and noise-substituted bands, which carry no coded spectrum (4.6.8.1.3 and 4.6.13.3: no mid/side on a band where either channel codes noise). [Musepack](musepack-subband-coding) flags each coded subband, the CELT layer of Opus each frame ([Opus modes](opus-modes)). Vorbis uses [square polar coupling](square-polar-coupling), [WavPack](wavpack-decorrelation) a flag per block, AC-3 a flag per band and block in 2/0 ([rematrixing](ac3-rematrixing)). FLAC picks left/side, side/right or mid/side for each frame ([channel assignment](flac-channel-assignment)). Monkey's Audio always codes a stereo pair as a mid and a side channel ([prediction](monkeys-audio-prediction)). Windows Media Audio versions 1 and 2 flag it in every block of a stereo stream ([frames and blocks](wma-frames-and-blocks)). DTS core frames can code the front pair (SUMF), the surround pair (SUMS) or, in the arrangement AMODE 3, the two channels as sum and difference ([audio coding header](dts-audio-coding-header)). TrueHD and MLP change their matrices per unit ([rematrixing](truehd-rematrixing)), ALAC mixes a pair by its `mixBits` and `mixRes` ([prediction](alac-prediction)), and TAK picks mid/side per frame ([frames](tak-frames)).

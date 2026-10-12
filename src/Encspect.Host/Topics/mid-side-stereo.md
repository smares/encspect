---
id: mid-side-stereo
title: Mid/side stereo
related: joint-stereo, intensity-stereo, channel-modes
references: ISO/IEC 11172-3 2.4.2.3 (mode_extension), 2.4.3.4 (Layer III decoding); ISO/IEC 13818-3; ISO/IEC 14496-3 4.6.8.1
---

**The problem.** In most stereo music the left and right channels are much alike: what sits in the middle reaches both speakers almost equally. Coding the two channels separately spends bits twice on what is nearly the same signal.

**How it works.** Mid/side stereo codes the sum and the difference of the channels instead. The mid channel carries what left and right share; the side channel carries only what differs between them. Where the channels are alike, the side channel is small and quiet, so it takes few bits, and the bits saved go to the mid channel. The decoder turns them back into left and right; the conversion itself loses nothing. Unlike [intensity stereo](intensity-stereo), nothing of the stereo image is given up; only the bits are spent differently.

**Per frame.** In MPEG audio Layer III, mid/side stereo is one of the [joint stereo](joint-stereo) tools, and the encoder decides for every frame whether to use it, through the Mode Extension field of the frame header. Where the channels differ a lot, left/right can cost fewer bits, so encoders switch. The Joint Stereo Modes statistics show how often a file uses each choice.

**What you see.** Mode Extension shows "Mid/side stereo" or "Mid/side and intensity stereo" in frames that use it. A file of joint stereo frames that never use mid/side stereo codes left and right in every frame, as plain stereo would.

**In AAC.** A channel pair with a common window chooses per scalefactor band: ms_mask_present 1 sends an ms_used flag for each band, 2 codes every band as mid/side (ISO/IEC 14496-3 4.6.8.1). Each CPE shows its mask and Mid/Side Bands; the statistics count the masks and the share of mid/side bands. Opus also chooses per band; Vorbis uses square polar coupling.

---
id: mid-side-stereo
title: Mid/side stereo
related: joint-stereo, intensity-stereo, channel-modes, musepack-subband-coding
references: ISO/IEC 11172-3 2.4.2.3 (mode_extension), 2.4.3.4 (Layer III decoding); ISO/IEC 13818-3
---

**The problem.** In most stereo music the left and right channels are very much alike: the singer, the bass and the drums sit in the middle and reach both speakers almost equally. Coding the two channels separately spends bits twice on what is nearly the same signal.

**How it works.** Mid/side stereo codes the sum and the difference of the channels instead. The mid channel carries what left and right share; the side channel carries only what differs between them. Where the channels are alike, the side channel is small and quiet, so it takes few bits, and the bits saved go to the mid channel. The decoder turns them back into left and right, as mid plus side and mid minus side; the conversion itself loses nothing. Unlike [intensity stereo](intensity-stereo), nothing of the stereo image is given up; only the bits are spent differently.

**Per frame.** In MPEG audio Layer III, mid/side stereo is one of the [joint stereo](joint-stereo) tools, and the encoder decides for every frame whether to use it, through the Mode Extension field of the frame header. Where the channels differ a lot, as with instruments panned hard to one side, coding left and right separately can cost fewer bits, so many encoders switch between the two. The Joint Stereo Modes statistics show how often a file uses each choice.

**What you see.** Mode Extension shows "Mid/side stereo" or "Mid/side and intensity stereo" in frames that use it. A file of joint stereo frames that never use mid/side stereo codes left and right in every frame, as plain stereo would.

AAC and Opus can choose it for each band, [Musepack](musepack-subband-coding) for each subband. Vorbis uses a related mapping, square polar coupling, which codes a magnitude and an angle.

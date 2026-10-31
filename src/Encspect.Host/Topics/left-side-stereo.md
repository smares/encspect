---
id: left-side-stereo
title: Left/side stereo
related: mid-side-stereo, flac-channel-assignment, alac-prediction
references: RFC 9639 (FLAC), section 9.2; Apple Lossless Audio Codec source (Apache License 2.0), matrix_dec.c
---

**The problem.** When one channel of a stereo pair is much simpler to code than the other, or when the sum of the channels is no easier than either channel, coding left and right as they are leaves little to gain, and [mid/side stereo](mid-side-stereo) can cost more than it saves.

**How it works.** Left/side stereo keeps the left channel as it is and replaces the right channel by the side channel, left minus right. The decoder gets the right channel back by subtracting the side channel from the left, so nothing is lost. The side channel is small where the two channels are alike, and the left channel is coded like any other. Side/right is the same with the right channel kept. The side channel needs one bit more than a channel, as the difference of two samples of the same size does.

**In FLAC.** The channel assignment of each two-channel frame can be left/side ([channel assignment](flac-channel-assignment)).

**In Apple Lossless.** A channel pair stores `mixBits` and `mixRes`. The second channel is always left minus right, and the first is a mix that weighs the left channel by `mixRes` out of 2 to the power of `mixBits`. When `mixRes` equals that power, the first channel is the left channel itself, which is left/side stereo; half of it gives the mean of both channels, which is mid/side ([prediction](alac-prediction)).

**What you see.** Stereo Coding counts the frames or channel pairs coded this way under "Left/side stereo".

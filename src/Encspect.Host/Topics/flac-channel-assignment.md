---
id: flac-channel-assignment
title: FLAC channel assignment
related: mid-side-stereo, flac-frames, flac-subframes, channel-layouts
references: RFC 9639 (FLAC), sections 9.1 and 9.2
---

**The problem.** The two channels of stereo music are usually alike, so coding their sum and difference can take fewer bits than coding each channel ([mid/side stereo](mid-side-stereo)). Not every passage profits, and the choice should follow the audio.

**How it works.** The channel assignment is a 4-bit field of the frame header. Values 0 to 7 mean 1 to 8 channels, each coded by itself, in the order of the layout the specification gives for that number. Three more values are for two channels: left/side, side/right and mid/side. The side channel is left minus right. The mid channel is the average of left and right, rounded down; the rounded-off bit is the low bit of the side channel, so the decoder restores it and loses nothing. An encoder can choose among the four assignments for every frame.

**The extra bit.** The side channel has one bit more than the others, as a difference of two samples of the same size does. Its subframe uses a sample size one larger. A stream of 32-bit samples therefore has a side channel of 33 bits, which a decoder must not hold in a 32-bit integer.

**What you see.** Channel Assignment in each frame header, and in the statistics how many stereo frames use each. A file of only independent frames was coded without decorrelation.

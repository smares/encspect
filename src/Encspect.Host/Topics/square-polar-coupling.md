---
id: square-polar-coupling
title: Square polar coupling
related: mid-side-stereo, vorbis-setup-header, channel-modes
references: Vorbis I specification, sections 1.3.2, 4.2.4 and 4.3.5
---

**The problem.** The channels of stereo or surround audio are much alike, and coding each one separately spends bits twice on what they share. Vorbis couples channels to save them, much as [mid/side stereo](mid-side-stereo) does in other codecs.

**How it works.** A mapping lists coupling steps, each a pair of channels. After the residue is decoded, one channel of the pair holds a magnitude and the other an angle, value by value. Square polar mapping turns them back into two channels: one channel takes the magnitude, the other the magnitude plus or minus the angle, and the signs of both values decide which channel gets which, four cases that correspond to the quadrants of a square. Where the two channels are nearly equal, the angle is close to 0 and takes few bits. The conversion itself loses nothing; what the encoder does to the angle before coding it decides how much stereo detail survives.

**Order matters.** Steps are undone in reverse order, so a channel may take part in several steps, which is how surround mappings couple more than two channels.

**What you see.** Each mapping's coupling steps with their magnitude and angle channels. Whether a packet really uses coupling, and how coarsely, sits in its residue, which the analyzer does not decode.

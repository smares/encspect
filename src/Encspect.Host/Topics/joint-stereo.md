---
id: joint-stereo
title: Joint stereo
related: mid-side-stereo, intensity-stereo, channel-modes
references: ISO/IEC 11172-3 2.4.2.3
---

**What it is.** Joint stereo is one of the four channel modes of an MPEG audio frame. It does not name one technique: it allows the encoder to code the two channels together, with the tools the Mode Extension field selects for each frame.

**The tools.** Layers I and II have one tool, [intensity stereo](intensity-stereo), above a bound the Mode Extension gives. Layer III has two, [mid/side stereo](mid-side-stereo) and intensity stereo, each switched on or off by one bit of the Mode Extension, so a joint stereo frame may use either, both or neither.

**Why a separate mode.** Plain stereo frames tell the decoder that every frame codes left and right independently. Joint stereo lets the encoder choose frame by frame, spending bits where the channels differ and saving them where they agree.

**What you see.** A joint stereo frame whose Mode Extension says "None" is coded as left and right, like a stereo frame. Many Layer III encoders mark every frame joint stereo and decide per frame between left/right and mid/side coding, so a file may show joint stereo throughout and mid/side stereo in only some frames. The Joint Stereo Modes statistics count the frames of each coding, and the Intensity Stereo Bounds statistics do the same for Layers I and II. Encoders often also record the stereo mode they were set to, as LAME does in its tag.

**The graphs.** Stereo Coding counts every frame, whatever its channel mode: left/right stereo, mid/side stereo, intensity stereo, both tools, dual channel and mono. Its graph over time shows the share of each per second, and the strip below it shows the coding of every frame. Joint Stereo Modes keeps to the joint stereo frames of Layer III. EncSpot counts every frame that is not joint stereo as its mode 0, so its mode 0 share is the Left/right stereo, Dual channel and Mono entries of Stereo Coding together.

**Channel Bit Share** shows how a stereo, joint stereo or dual channel frame divides its audio bits between the two channels: the first channel's share, left or mid, per frame and per second. In a mid/side frame it is the mid channel's share.

---
id: emphasis
title: Emphasis
related: frame-header
references: ISO/IEC 11172-3 2.4.2.3
---

**What it was for.** Emphasis is a technique from analog recording and early digital audio: before recording, high frequencies are boosted by a fixed curve, and on playback they are lowered by the opposite curve. Noise added between the two steps, which is strongest in the high frequencies, is lowered along with them. Some early CDs used the 50/15 µs curve this way.

**In MPEG audio.** The emphasis field of the frame header tells the decoder which curve, if any, the audio was given before encoding, so it can undo it: none, 50/15 µs, or CCITT J.17, a curve from broadcast transmission. One value is reserved.

**What you see.** "None" is almost universal, since perceptual coding has its own ways to keep noise inaudible. A file marked with emphasis was usually made from a source that had it, and plays with too much treble in a player that does not undo it. The reserved value is reported as a warning.

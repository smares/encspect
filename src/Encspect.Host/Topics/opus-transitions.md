---
id: opus-transitions
title: Opus mode changes and redundant frames
related: opus-modes, opus-celt, opus-silk, gapless-playback
references: RFC 6716, sections 4.5 and 4.5.1; RFC 7845, sections 4.2 and 4.6
---

**The problem.** SILK and CELT keep different state, so a stream that changes from one to the other (or changes SILK's bandwidth) has no history to continue from. The change is audible as a click unless the decoder bridges it.

**Redundant frames.** The encoder can end a frame with a short CELT frame of 5 ms, coded apart from the first (a redundant frame). A decoder fades between it and the main frame over 2.5 ms: before the frame when CELT comes first, after it when SILK or the hybrid frame comes first. A redundant frame costs bytes, so encoders write one at the changes where bridging matters most.

**Changes without redundancy.** RFC 6716 specifies the result only where a redundant frame bridges the change. Otherwise it recommends that a decoder conceal the missing 5 ms of the old mode and fade from them into the new frame, which the reference decoder does. The analyzer follows it, so that its samples at a change equal the reference decoder's (decision 6 of its design). Frames that are lost or hold no data are different: the analyzer never conceals them.

**Pre-roll.** RFC 7845 asks a player that seeks to start 80 ms early, "sufficient history ... that it will have converged". The analyzer starts over after a gap or a frame without data and holds back that much output.

**What you see.** The statistics count the frames that end with a redundant frame; the figures of the measurements say how many samples were withheld after a gap and why.

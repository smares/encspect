---
id: opus-silk
title: Opus SILK: linear prediction for speech
related: opus-modes, linear-prediction, opus-range-coder, mid-side-stereo
references: RFC 6716, section 4.2
---

**The problem.** Speech has a spectral envelope, which the vocal tract shapes, and a pitch, which the vocal cords set. SILK codes both as parameters and spends its bits on the part that is left, which a transform cannot do as cheaply at low rates.

**What a frame holds.** Every 10 or 20 ms a SILK frame states whether it carries voice activity and of what kind (none, unvoiced or voiced), a gain for each 5 ms subframe, and the spectral envelope as line spectral frequencies, coded against a codebook and interpolated between the two halves of a 20 ms frame. A voiced frame adds a pitch lag, with its change over the frame, and five long-term prediction taps for each subframe. The rest is the excitation: a count of pulses for every block of 16 samples, split by a binary tree and followed by their signs.

**Stereo and rates.** Stereo is coded as a mid and a side channel with a prediction weight for the side from the mid; a frame may code the mid channel alone. SILK always works at 8, 12 or 16 kHz, according to the bandwidth, and the decoder resamples to 48 kHz. A frame can also hold a low-bitrate copy of an earlier frame (LBRR), for the decoder to use if that frame is lost; the analyzer reads and drops it.

**What you see.** The analyzer reads every frame's indices and pulses to the end, which gives the packet's final range coder state. SILK's own output, before the resampler, is integer arithmetic that RFC 6716 defines exactly; the resampler to 48 kHz is not normative (section 4.2.9: a decoder can use any method), so the analyzer's samples follow libopus's.

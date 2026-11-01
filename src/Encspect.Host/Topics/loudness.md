---
id: loudness
title: Loudness, LUFS and gating
related: loudness-range, sample-peak-and-true-peak, levels, measurements, bwf-broadcast-extension, replaygain, dialogue-normalization
references: ITU-R BS.1770-5 Annex 1 and Annex 3; EBU R 128; EBU Tech 3341
---

**The problem.** Peak and RMS levels say little about how loud a program sounds: a bass-heavy and a bright signal of the same RMS level are not equally loud, and silence lowers an average. Broadcasters needed one figure that follows hearing and that programs can be normalized to.

**How it works.** ITU-R BS.1770 defines it in steps.

- **K-weighting:** a high-shelf filter, which models the effect of the head and raises the level above about 2 kHz by 4 dB, and a highpass filter at about 38 Hz. The Recommendation gives coefficients for 48 kHz; at other rates the analyzer derives ones with the same response, from 8,000 Hz.
- **Blocks:** the mean square of each channel over 400 ms, a block every 100 ms. The channels are weighted by their loudspeaker positions (1.00 for the front channels, 1.41 for the surround channels, the LFE left out) and summed. Loudness is -0.691 + 10 log10 of that sum, in LUFS (BS.1770 writes LKFS), one unit for each dB.
- **Gating:** a block counts only if it is above -70 LUFS, and then only if it is above a level 10 LU below the loudness of the blocks that passed the first gate, so silence does not pull a program down. Each block is compared with the gate on its own. Only a stream of more than about 3.6 hours is gated on bins of 0.01 LU, which can misjudge blocks sitting at the gate by up to -10 log10(1 - p) LU (p: the share of passing blocks in the gate's bin); the report then says so.

EBU R 128 names three scales: momentary (400 ms), short-term (3 s) and integrated (the whole program, gated), and asks for -23 LUFS.

**What you see.** Integrated Loudness, Loudness Range, Maximum Momentary Loudness (taken every 20 ms) and Maximum Short-Term Loudness (every 100 ms), computed from the decoded audio. They are measured; a loudness a file states about itself, such as a Broadcast Wave bext value, is another figure and is not compared with them. A stream of three or more channels must state its channel positions, since the weights follow them.

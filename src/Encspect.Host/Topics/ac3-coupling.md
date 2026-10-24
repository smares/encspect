---
id: ac3-coupling
title: Channel coupling in AC-3
related: intensity-stereo, ac3-audio-blocks, ac3-rematrixing, eac3-spectral-extension
references: ATSC A/52:2018 5.4.3.7 to 5.4.3.18, 7.4; Annex E 3.5
---

**The problem.** At the high frequencies of several channels, the ear follows mostly how loud each channel is, not the exact waveform, as with [intensity stereo](intensity-stereo). Coding each channel's highs separately spends bits on detail few listeners hear.

**How it works.** Above a coupling begin frequency, the channels in coupling are added into one coupling channel, which is coded once. For each coupling band, a group of 12-coefficient sub-bands, every coupled channel gets a coupling coordinate: how much of the coupling channel it gets back. The decoder multiplies the coupling channel by each coordinate to rebuild the channels' high frequencies. In 2/0, phase flags may invert the right channel's copy in a band.

**The relation to intensity stereo.** Coupling is the same idea as intensity stereo in MPEG audio: one shared signal plus a level per channel and band. AC-3 applies it to any two or more of up to five channels, and keeps the fine timing of each channel below the coupling frequency. What is given up, the differences in waveform between the channels in the coupled bands, cannot be restored.

**What you see.** Whether coupling is in use in each block, which channels take part, the begin and end codes and band structure, each channel's coordinates, and the coupling channel's own exponents and mantissas. The statistics count the blocks with coupling, and the graph Blocks With Coupling shows them under Stereo Coding. E-AC-3 adds enhanced coupling, which also codes a phase angle per band; the analyzer stops reading a frame where it is used.

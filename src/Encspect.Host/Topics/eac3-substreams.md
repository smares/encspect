---
id: eac3-substreams
title: E-AC-3 substreams
related: ac3-frames-and-bsi, channel-layouts, eac3-spectral-extension
references: ATSC A/52:2018 Annex E 2.3.1.1, 2.3.1.2, 2.3.1.7, 2.3.1.8; ETSI TS 102 366 F.2, F.6
---

**The problem.** One AC-3 bit stream carries one program of at most 5.1 channels. Broadcasters wanted 7.1 and more, and several programs, such as languages or a described version, in one stream.

**How it works.** Every E-AC-3 frame names a stream type and a substream ID. Independent substreams, IDs 0 to 7, are programs a decoder can play alone; substream 0 must come first. Each may be followed by up to eight dependent substreams, which add channels to it or replace some of them. A dependent substream's custom channel map names the locations of its channels, such as the rear surround pair that turns a 5.1 program into 7.1. An AC-3 frame may stand in as independent substream 0, so legacy decoders still play the core. Substreams of a program share its sample rate and blocks per frame.

**What you see.** Each frame's stream type and substream ID, and a dependent substream's channel map. Each independent substream is a stream of its own, whose channel layout combines its channels with the locations its dependent substreams add; 5.1's surround pair becomes the side pair when a rear pair joins it. In MP4, a sample holds the frames of every substream for six blocks, and the dec3 box lists the independent substreams with their dependents' channel locations.

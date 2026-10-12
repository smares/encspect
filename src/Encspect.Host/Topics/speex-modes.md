---
id: speex-modes
title: Speex modes and submodes
related: granule-position, bitrate-modes, vorbis-comments, ogg-pages
references: Speex manual, sections 2.1, 5.5, 7.3, 9.3 and 10.4; libspeex 1.2.1 (speex_mode_query)
---

**What Speex is.** Speex is a speech codec for voice over IP, built on CELP, code-excited linear prediction. The encoder models the vocal tract as a filter found by linear prediction, and codes the excitation that drives it with a pitch predictor and a codebook search, choosing what brings the synthesized speech closest to the input under a perceptual weighting. Every frame lasts 20 ms.

**Modes as layers.** Narrowband codes 8 kHz audio in frames of 160 samples. Wideband, at 16 kHz, splits the signal into two bands: the low band is coded as a narrowband frame, and a wideband layer adds the band from 4 to 8 kHz, so a narrowband decoder can still decode the low band. Ultra-wideband, at 32 kHz, adds a layer for 8 to 16 kHz the same way.

**Submodes.** Each layer starts with its submode, which fixes its bit allocation and so the bitrate. Narrowband submodes 1 to 8 take between 43 and 492 bits per frame, 2.15 to 24.6 kbps; submode 0 is a 5-bit frame. Wideband layers have submodes 0 to 4, ultra-wideband layers 0 and 1. In place of a narrowband submode, code 14 carries in-band requests and parameters, such as the intensity stereo of a stereo stream, 13 carries application data, and 15 ends the packet.

**Bitrate control.** In VBR the encoder picks each frame's submodes by what the speech needs; ABR steers VBR toward a target average. Voice activity detection codes non-speech with just enough bits for the background noise; discontinuous transmission codes stationary noise in 5-bit frames, as a file cannot stop.

**What you see.** The header's mode, frames per packet and VBR flag, the submode of every layer in every frame, and in the statistics how many frames use each submode.

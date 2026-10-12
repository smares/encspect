---
id: opus-modes
title: Opus modes: SILK, CELT and hybrid
related: opus-toc, opus-pre-skip, mid-side-stereo, intensity-stereo, speex-modes
references: RFC 6716, sections 1, 2, 3.1, 4.2 and 4.3
---

**Why two coders.** Opus combines a layer based on linear prediction, derived from Skype's SILK codec, with a layer based on the MDCT, derived from the CELT codec. Linear prediction techniques such as CELP code the low frequencies of speech more efficiently than a transform does, while for music and the higher frequencies of speech the transform does better. A codec with both can cover a wider range than either one alone.

**The three modes.** SILK-only codes narrowband, mediumband or wideband audio, up to 8 kHz, in frames of 10 to 60 ms, for speech at low bitrates. CELT-only codes narrowband to fullband audio, up to 20 kHz, in frames of 2.5 to 20 ms, for music and for speech at very low delay. Hybrid mode runs both at once on super-wideband or fullband audio in frames of 10 or 20 ms: SILK codes everything below 8 kHz and CELT the bands above, with no overlap between them. The encoder may switch mode, bandwidth and frame size from one packet to the next, and every packet's TOC byte says which it uses.

**Stereo.** SILK codes stereo as a variant of mid/side, so a mono decoder can decode the mid channel alone. CELT chooses per frame between [mid/side stereo](mid-side-stereo), [intensity stereo](intensity-stereo) above a start band, and dual stereo, which codes left and right separately. These choices sit inside the coded frames, which the analyzer does not decode.

**What you see.** The statistics count the frames of each mode, bandwidth and frame size, so a file shows whether its encoder treated it as speech, music or both, and each packet's TOC names its configuration.

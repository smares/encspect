---
id: speex-excitation
title: Speex excitation: pitch and codebooks
related: speex-modes, line-spectral-pairs, speex-enhancer, linear-prediction
references: Speex manual, sections 2.1 and 9; libspeex 1.2.1 (nb_celp.c, ltp.c, cb_search.c)
---

**The problem.** A filter found by linear prediction shapes the sound of a voice, but something must drive it. Speex calls that signal the excitation and codes it in the parts it names below, so that the filter's output is the speech.

**Pitch.** Voiced speech repeats with the period of the vocal cords. The decoder keeps the last 144 samples of excitation; each subframe of 5 ms (40 samples in narrowband) adds the excitation one pitch period back, weighted by three taps whose values come from a codebook of 32 or 128 entries. The lag has 7 bits (17 to 144 samples) or, in low-rate submodes, is the stream's open-loop lag or a few steps around it.

**Innovation.** What pitch cannot predict is a sum of entries of a shape codebook: the subframe is split into subvectors (8 of 5 samples, 4 of 10, 2 of 20 in narrowband) and each takes one entry. A gain correction of 1 or 3 bits scales the subframe's entries against the frame's global gain of 5 bits. Submode 7 codes a second set of entries at a smaller gain. The wideband layers code the band from 4 kHz up the same way, without pitch, and a layer of submode 1 codes only a gain and reuses the lower band's innovation.

**Comfort noise.** Submode 1 of narrowband codes no entries: its innovation is noise from libspeex's generator, a pulse train at the open-loop lag mixing in at a gain given by 4 bits. Frames of submode 0 carry nothing, and the decoder makes noise at the last level.

**What you see.** The submode of each layer in each frame decides which of these parts the frame holds; the decoder reads their values from the parser's entropy pass.

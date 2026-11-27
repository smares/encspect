---
id: aac-prediction
title: Prediction in AAC Main
related: aac-object-types, aac-windows-and-blocks, tns, mid-side-stereo, intensity-stereo, pns
references: ISO/IEC 13818-7 13.1 to 13.3 (Tables 62 and 63); ISO/IEC 14496-3 4.6.8.2.4, 4.6.13.4
---

**The problem.** In a steady tone the same spectral line holds nearly the same value from one long window to the next, so coding every value from scratch repeats what the decoder could already guess. Only AAC Main uses a tool for this, which is why Main streams are the ones that carry it.

**How it works.** Each spectral line up to a limit that depends on the sample rate (at most 672 lines, ISO/IEC 13818-7 Table 62) has a predictor of its own in the encoder and in the decoder. It is a second-order lattice filter that adapts by least mean squares from the line's reconstructed values in the two frames before, so no coefficients are sent. The encoder sends the prediction error instead of the value in the bands whose prediction_used flag is set, and the decoder adds its estimate back. Every predictor runs in every long window, used or not, so that it follows the signal; an eight-short window resets all of them. The specification fixes the arithmetic down to the bits (single precision, states cut to 16 bits), so that encoder and decoder stay in step.

**Resets.** Predictors drift apart after any loss, so the encoder resets them in 30 interleaved groups (line k is in group k mod 30 plus 1), one group named by predictor_reset_group_number in a frame. A group is to be reset within 240 frames in which it is used. A stream that is cut or spliced starts with predictors out of step, and prediction is right again for a line once its group is reset.

**With other tools.** Prediction follows mid/side stereo and noise substitution. A band of noise substitution switches prediction off and resets its predictors.

**What you see.** predictor_data_present in each long window's ics_info, with the reset group and the bands that use prediction. The statistics count the channel streams with predictor data and the share of bands that use it.

**Where it is specified.** ISO/IEC 13818-7 clause 13; ISO/IEC 14496-3 4.6.8.2.4 and 4.6.13.4 for its interplay with intensity stereo and noise substitution.

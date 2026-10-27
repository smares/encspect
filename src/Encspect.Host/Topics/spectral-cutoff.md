---
id: spectral-cutoff
title: Spectral cutoff
related: spectrogram, measurements, lowpass, coded-bandwidth, noise-shaping
references: Encspect design, docs/design/spectral-analysis.md
---

**The problem.** Encoders filter out high frequencies, and the cut stays in the audio when it is decoded and stored losslessly.

**How it works.** From 2 kHz up, the analyzer compares the mean level of the 1 kHz of rows under each row with the 1 kHz above it; the largest difference is the step. The cutoff is the highest row more than 6 dB above the level above the step. It is reported when the step is at least 25 dB and at least half of the frames with signal show a step of 15 dB there, so quiet passages do not count against it. Rates below 8,000 Hz are not searched.

**What it shows.** An edge fits an encoder's lowpass, a mastering filter, resampling or an anti-alias filter, which the measurement cannot tell apart. No edge shows nothing either: dither over a quiet recording, or a part of a file from another source, can hide one. The edge can read high when the band above the step holds energy from only a few frames, as at the cut from a lowpassed passage to silence. It is not the [lowpass frequency](lowpass) an encoder states, nor the [coded bandwidth](coded-bandwidth) the coded data states.

**Observations.** In Encspect's test files, 1 s of mono pink noise encoded and decoded again, a cutoff was found for LAME 3.100 at -b 64 (17,097 Hz, one row above its printed band of 16,538 to 17,071 Hz), -b 96, -b 160, -V 2 and -V 5, oggenc 1.4.2 at -q 2 and -q 5, FFmpeg 6.1.1's aac at 48 to 96 kbps and opusenc 1.4 at 48 kbit/s; none for LAME -V 0 and oggenc -q 8. Lowpass sections of 2, 4 and 8 poles at 16 kHz give steps under 25 dB. The analyzer uses none of this to name an encoder.

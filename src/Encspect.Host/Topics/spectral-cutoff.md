---
id: spectral-cutoff
title: Spectral cutoff
related: spectrogram, measurements, lowpass, coded-bandwidth, noise-shaping
references: Encspect design, docs/design/spectral-analysis.md
---

**The problem.** Encoders filter out high frequencies, and the cut stays in the audio when it is decoded and stored losslessly.

**How it works.** From 2 kHz up, the analyzer compares the mean level of the 1 kHz of rows under each row with the 1 kHz above it; the largest difference is the step. A cutoff is reported when the step is at least 25 dB and at least half of the frames with signal show a step of 15 dB there, so quiet passages do not count against it. It lies where the frames' own steps end: the highest row at which at least half of them show one, less the 1 kHz of the band. Rates below 8,000 Hz are not searched.

**What it shows.** An edge fits an encoder's lowpass, a mastering filter, resampling or an anti-alias filter, which the measurement cannot tell apart. No edge shows nothing either: dither over a quiet recording, or a part of a file from another source, can hide one. A few frames that differ do not move it: in Encspect's test files, 16 kHz lines followed by digital silence read 16,021 Hz. It is neither the [lowpass frequency](lowpass) an encoder states nor the [coded bandwidth](coded-bandwidth).

**Observations.** In Encspect's test files, 1 s of mono pink noise encoded and decoded again, a cutoff was found for LAME 3.100 (-b 64 to 160, -V 2 and 5), oggenc 1.4.2 (-q 2 and 5), FFmpeg 6.1.1's aac and opusenc 1.4; none for LAME -V 0 and oggenc -q 8. For each LAME file at 44.1 and 48 kHz it lies inside the transition band that lame --verbose prints (-b 64 at 44.1 kHz: 16,796 Hz in 16,538 to 17,071 Hz); the 22.05 kHz file prints none. Lowpass sections of 2, 4 and 8 poles at 16 kHz give steps under 25 dB. The analyzer names no encoder from it.

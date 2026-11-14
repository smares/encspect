---
id: spectral-cutoff
title: Spectral cutoff
related: spectrogram, spectral-bandwidth, measurements, lowpass, coded-bandwidth, noise-shaping
references: Encspect design, docs/design/spectral-analysis.md
---

**The problem.** Encoders filter out high frequencies, and the cut stays in the audio when decoded and stored losslessly.

**How it works.** From 2 kHz up, the analyzer compares the mean level of the 1 kHz of rows under each row with the 1 kHz above it; the largest difference is the step. A cutoff is reported when the step is at least 25 dB and at least half of the frames with signal, those within 30 dB of the level 1 % of the frames reach, show a step of 15 dB there. The cutoff is where the frames' own steps end: walking up from the step, the last row at which half of them still show one, less 1 kHz (the lowest row of the band under it). Rates under 8,000 Hz are not searched.

**Partial cutoff.** An edge only part of the stream has (lossy tracks joined with others) is hidden in the average. A frame shows a ceiling at a row where the 1 kHz under it is at least 25 dB above the mean power of everything above. The partial cutoff is the row where most frames with signal show one, found from a fifth of them; the text prints it only where the cutoff is not found or lies over 1 kHz away.

**What it shows.** An edge fits an encoder's lowpass, a mastering filter, resampling or an anti-alias filter, which it cannot tell apart. No edge shows nothing either: dither over a quiet recording, broadband clicks or a part from another source can hide one. It is neither the [lowpass frequency](lowpass) an encoder states nor the [coded bandwidth](coded-bandwidth).

**Observations.** In Encspect's test files, 1 s of mono pink noise encoded and decoded again, a cutoff was found for LAME 3.100, oggenc 1.4.2 (-q 2, 5), FFmpeg 6.1.1's aac and opusenc 1.4, but not for LAME -V 0 or oggenc -q 8. Every LAME one at 44.1 and 48 kHz lies inside the band that lame --verbose prints; the 22.05 kHz file prints none. Lines at 16 kHz for 20, 50 and 80 % of a stream, white noise for the rest: a ceiling in 3, 10 and 16 of 20 frames, found for the last two. Pink noise through one and four lowpass sections: 5 and 8 of 20, so a minority of frames is weak evidence.

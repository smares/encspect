---
id: spectral-cutoff
title: Spectral cutoff
related: spectrogram, measurements, lowpass, coded-bandwidth, noise-shaping
references: Encspect design, docs/design/spectral-analysis.md
---

**The problem.** Encoders filter out high frequencies, and the cut stays in the audio when it is decoded and stored losslessly. The edge is visible in the average spectrum.

**How it works.** Over the rows of the [average spectrum](spectrogram) from 2 kHz up, the analyzer takes the mean level of a band of 1 kHz under each row and of a band above it; the largest difference is the step. The cutoff is the highest row more than 6 dB above the level above the step. A cutoff is reported when the step is at least 25 dB and at least half of the frames with signal show a step of 15 dB of their own there, so quiet passages do not count against it. The report gives the frequency, the step, where the transition starts and the frames that show it. Rates below 8,000 Hz are not searched.

**What it shows.** An edge is consistent with an encoder's lowpass, a mastering filter, resampling or an anti-alias filter, and the measurement cannot tell them apart. No edge shows nothing either: dither or noise over a quiet recording, or a part of a file from another source, can hide one. The cutoff is not the [lowpass frequency](lowpass) an encoder states, nor the [coded bandwidth](coded-bandwidth) the coded data states.

**Observations.** In Encspect's test files, 2 s of pink noise encoded and decoded again, a cutoff was found for LAME 3.100 at -b 128 (17,097 Hz; the transition band it prints is 16,538 to 17,071 Hz), -b 192, -b 320, -V 2 and -V 5, for oggenc 1.4.2 at -q 2 and -q 5, for FFmpeg 6.1.1's aac at 96, 128 and 192 kbps and for opusenc 1.4 at 96 kbit/s; none for LAME -V 0 and oggenc -q 8. Exceptions: at 48 kHz LAME's cutoff lies 77 Hz above its printed band, at 22.05 kHz 530 Hz below its start. Pink noise through lowpass sections of 2, 4 and 8 poles at 16 kHz gives steps under 25 dB. The analyzer does not use any of this to name an encoder.

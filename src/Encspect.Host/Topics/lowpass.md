---
id: lowpass
title: Lowpass filtering
related: ath, lame-tag, bitrate-modes
references: LAME documentation; LAME tag specification (Gabriel Bouvigne)
---

**The problem.** High frequencies cost many bits to code, and at a limited bitrate those bits are missing elsewhere. At the same time, hearing is least sensitive at the top of the range, and many adults hear little above 15 to 17 kHz.

**How encoders handle it.** Most encoders filter out everything above a cutoff frequency before encoding, a lowpass filter. The lower the bitrate, the lower the cutoff they choose, so the bits go to the range where they matter most. LAME picks its lowpass from the bitrate or quality setting unless told otherwise, and many encoders do the same in their own way.

**What you see.** LAME records its cutoff in the LAME tag's Lowpass field, in steps of 100 Hz. The analyzer does not decode the audio, so it cannot measure the cutoff of other files; a spectrum view of decoded audio would show it as an edge above which the spectrum is empty. A low cutoff at a high bitrate can point to audio that was encoded more than once, but it can also be a deliberate setting.

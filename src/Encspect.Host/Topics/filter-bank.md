---
id: filter-bank
title: The filterbank and the MDCT
related: mpeg-layers, block-switching, subband-coding, granules-and-frames
references: ISO/IEC 11172-3 2.4.3.2 to 2.4.3.4
---

**The idea.** A perceptual coder does not code the waveform directly. It splits the sound into frequency components, because the ear's sensitivity depends strongly on frequency, and because a loud tone hides quieter sounds near its own frequency. With the sound split up, the encoder can spend bits where they are audible and save them where they are not.

**The polyphase filterbank.** All three layers first split the audio into 32 subbands of equal width with a polyphase filterbank. At 44.1 kHz each subband is about 689 Hz wide. Layers I and II quantize and code these subband samples directly ([subband coding](subband-coding)).

**The MDCT in Layer III.** Equal bands of 689 Hz are coarse at low frequencies, where our hearing separates pitches finely. Layer III therefore applies a second transform, the modified discrete cosine transform (MDCT), to each subband, which splits it into 18 frequency lines: 576 lines per granule in all. This combination is called a hybrid filterbank.

**Time against frequency.** A long transform resolves frequency finely but smears sound over its length in time. For sudden sounds, Layer III switches to three short transforms of 6 lines per subband ([block switching](block-switching)). The Block Type field of each granule shows which was used.

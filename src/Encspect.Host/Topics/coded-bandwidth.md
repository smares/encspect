---
id: coded-bandwidth
title: Coded bandwidth
related: lowpass, subband-coding, filter-bank, mpeg-layers
references: ISO/IEC 11172-3 2.4.1.5, 2.4.1.6, 2.4.2.5, 2.4.2.6
---

**The problem.** Encoders spend no bits on frequencies they filtered out or chose not to code. The coded data of a frame says where the coding stopped, so the analyzer can show the reach of a stream without decoding it.

**In Layers I and II.** The bit allocation of every frame gives each of the 32 subbands of the [filterbank](filter-bank) a number of bits, or none, which leaves the subband out ([subband coding](subband-coding)). Subbands are equal slices of the range up to half the sample rate, each a 64th of the sample rate wide. The coded bandwidth of a frame is the upper edge of its highest subband with an allocation other than none, in either channel: at 44,100 Hz, subband 15 ends at 11,025 Hz and subband 26 at 18,605 Hz. The Coded Bandwidth statistics give the lowest, most common and highest value of a file's frames, and the graph shows each frame and each second of audio.

**A bound the data states.** The value is what the frame's syntax declares, not a measurement of the sound. A subband below the bound can have no bits, and one within it can carry very little, so a quiet passage may code a wide bandwidth.

**Not the lowpass.** Three figures answer different questions and are kept apart. The [lowpass](lowpass) in a LAME tag is the setting the encoder filtered with. The coded bandwidth is where the coded data reaches. A lowpass measured from decoded audio would show where the spectrum is empty, which needs a decoder.

**Layer III** has no such field in its side information. Where its coded spectrum ends lies in the Huffman-coded data, which the analyzer does not walk, so the Coded Bandwidth statistics and graph appear for Layer I and II files only.

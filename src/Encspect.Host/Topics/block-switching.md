---
id: block-switching
title: Block types and block switching
related: filter-bank, granules-and-frames, scfsi
references: ISO/IEC 11172-3 2.4.2.7, 2.4.3.4
---

**The problem: pre-echo.** Layer III transforms each granule of 576 samples, about 13 milliseconds at 44.1 kHz, into frequency lines. Quantization noise then spreads over the whole length of the transform. Where a quiet passage ends in a sudden attack, the noise the attack's bits cause spreads to before the attack, into the quiet part, where the ear hears it as a soft smear that precedes the sound: pre-echo.

**Short blocks.** For such moments the encoder switches the granule to short blocks: three transforms of 192 lines each, a third of the length, so the noise stays close to the attack, where the attack itself masks it. Short blocks resolve frequency more coarsely, so encoders use them only where needed.

**Start and stop windows.** The transform's windows overlap with their neighbors, so a long block cannot jump straight to short ones. A start block (type 1) leads from long to short, a stop block (type 3) back from short to long. Type 0 is a normal long block, type 2 short.

**Mixed blocks.** With mixed_block_flag set, a short block keeps long transforms for the two lowest subbands, where frequency resolution matters most, and uses short ones above.

**What you see.** Each granule's Block Type, and the Block Usage statistics, which count granules per type as mp3guessenc and EncSpot do. Encoders differ in how often they switch.

**The graphs.** Block Usage shows the type of every granule and channel, the share of each type per second and a strip with one cell per granule and channel. Block Sizes groups the same granules by the size of the transform, as EncSpot counts them: long (normal, start and stop blocks), short, mixed, and the reserved type apart, which EncSpot counts as long.

**In AAC.** The four window sequences of [AAC](aac-windows-and-blocks) correspond to Layer III's: only long to normal, long start to start, eight short to short, long stop to stop. AAC has no mixed blocks. Window Sequences and Block Sizes count every channel stream, as Block Usage counts every granule and channel.

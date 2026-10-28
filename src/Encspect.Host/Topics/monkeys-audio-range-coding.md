---
id: monkeys-audio-range-coding
title: Monkey's Audio range coding
related: monkeys-audio-frames, monkeys-audio-prediction, rice-coding, huffman-coding
references: Monkey's Audio SDK 13.42, Source/MACLib/UnBitArray.cpp, UnBitArrayBase.h, BitArray.cpp and Old/UnBitArrayOld.cpp
---

**The problem.** After prediction, what is left of each sample is a small signed number that is usually near zero, and the number of bits it takes should follow how large it is.

**How it works.** Monkey's Audio codes each value with a range coder, an arithmetic code that spends a fraction of a bit where a symbol is likely. A value is split in three. A fixed model of 64 symbols, with probabilities that favor small numbers, codes the high part; the low part is a number of bits below a pivot, which follows the average size of the recent values of the channel, so the code adapts to loud and quiet passages as a [Rice code](rice-coding) does. Larger values escape through an overflow symbol. Files of version 3.90 to 3.98 use the same range coder with another model of 64 symbols, and no pivot: the symbol and the number of bits below it follow the average size of the channel's values, and the last symbol announces a value with its own width. Files of version 3.90 to 3.92 use the range coder an array of one channel at a time; files before 3.90 use a [Rice code](rice-coding) whose parameter follows a running sum of the values instead. The decoder keeps a 32-bit range and renormalizes by a byte at a time, so it reads the frame's bytes in order.

**What you see.** Nothing in the nodes: the range coder lives inside the bytes of a frame, and the analyzer reads them only when it decodes the audio. Its cost shows in the sizes. The cheapest symbol of the model takes about 1.74 bits, so even a sample the predictor gets exactly right costs about that much, and a quiet file does not shrink below it; digital silence is the exception, because its frames are [special](monkeys-audio-frames).

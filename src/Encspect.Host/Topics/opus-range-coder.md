---
id: opus-range-coder
title: The Opus range coder
related: opus-silk, opus-celt, huffman-coding
references: RFC 6716, sections 4.1 and 6
---

**The problem.** SILK and CELT code their symbols with probabilities that differ from symbol to symbol and from frame to frame, often so skewed that a whole bit per symbol would be too much. A range coder spends exactly what a symbol's probability says, in fractions of a bit.

**How it works.** The coder keeps a range, a number of 32 bits that narrows with every symbol in proportion to the symbol's probability and is refilled a byte at a time from the front of the frame. Raw bits, which CELT uses for the lowest bits of its energies and for flags, are read from the back of the frame instead, so the two meet in the middle. A frame is therefore complete only when the decoder has read every symbol; a symbol read past the end reads zeros.

**The final state.** After the last symbol of a frame the range is a 32-bit number that depends on every symbol read. RFC 6716 (section 6) has a conforming decoder end with the same range as the encoder did, and the reference decoder's test vectors store it for each packet. It is the strongest check a lossy format has: a decoder that is wrong about a single symbol ends elsewhere. For a packet whose frames carry a redundant CELT frame the state is the exclusive or of both ranges.

**What you see.** The analyzer reads every frame's symbols and ends each frame with the range the format defines, which the tests hold against the reference decoder's.

---
id: wma-bit-reservoir
title: The bit reservoir in Windows Media Audio
related: wma-superframes, bit-reservoir, bitrate-modes
references: FFmpeg 6.1.1 libavcodec/wmadec.c (wma_decode_superframe)
---

**The problem.** Superframes have a fixed size, but frames need more or fewer bits as the music does. A frame that does not fit would have to be cut or padded.

**How it works.** When the encode options turn the bit reservoir on, a frame is not tied to a superframe. It begins wherever the frame before it ended, and may run on into the next superframe. The decoder keeps the unfinished frame's bits, which are its reservoir, and when the next superframe arrives it reads the number of bits the header gives and joins them to what it kept. The bits that were left over at the end of a superframe are thereby spent in the next one, as quiet passages leave room for loud ones. This differs from the [bit reservoir of MP3](bit-reservoir), where a frame's data starts in earlier frames; here a frame starts in one superframe and ends in a later one.

**What you see.** The frames that span superframes, the largest and average reservoir and its size after every superframe on the graph. A superframe may lie entirely inside a long frame, and then no frame ends in it. The bits left after the last frame when the stream stops are reported. Microsoft's encoders also leave bits between the end of a frame that spans superframes and the bit offset the next header states, zero bits or more coded data; a decoder skips them, and they are counted as filler. If a superframe is lost or damaged, the frame that spans it is lost too, and a decoder drops its reservoir.

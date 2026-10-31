---
id: wma-voice-frames
title: Windows Media Audio Voice frames
related: wma-voice, wma-voice-superframes
references: FFmpeg 6.1.1 libavcodec/wmavoice.c
---

**The problem.** A speech coder sends the filter and the excitation of every few milliseconds, and how much of each depends on the sound: silence needs almost nothing, voiced speech needs a pitch.

**Line spectral pairs.** The filter is sent as 10 or 16 line spectral pairs (LSPs), as the flags say: 24 or 34 bits for each frame, or 48 or 60 bits once per superframe for all three frames when the packet sets the residual flag.

**Frame types.** A frame of 160 samples starts with a code of 2 to 14 bits. The table in the codec data maps the code to one of 17 frame types, which fix three things: the blocks per frame (1, 2, 4 or 8), the adaptive codebook (none, a pitch for the frame, or a pitch for each block) and the fixed codebook (comfort noise, a hardcoded codebook, pitch-adaptive window pulses, or sets of five pulses of which 0, 2 or 5 hold a pair). The pitch is a delay in samples.

**Blocks.** Each block then sends its excitation: positions and signs of pulses, and a gain index. Their widths follow from the type, except that the first pulse of a pitch-adaptive window frame decides how the frame's bits divide between its two blocks, and that the pitch such a frame is read with depends on the frame before.

**What you see in a file.** Each frame's type, pitch and block sizes, with statistics of the types. The analyzer reads the excitation only to find where a frame ends and does not report it.

**In Encspect's test files.** The hand-built files use every frame type. No file from the Windows Media Format SDK's writer uses a code of 14 bits. The analyzer does not use this.

---
id: bitrate-modes
title: CBR, VBR and ABR
related: bit-reservoir, xing-header, lame-tag, padding
references: ISO/IEC 11172-3 2.4.2.3; LAME documentation
---

Every MPEG audio frame states its own bitrate, so a stream can use one bitrate throughout or change it from frame to frame.

**CBR**, constant bitrate, uses the same bitrate in every frame. Frames have nearly the same size, the file's size grows evenly with time, and a player can find any point by simple arithmetic. In Layer III the [bit reservoir](bit-reservoir) still moves bits between frames, so the audio inside a CBR stream is not coded at a constant rate.

**VBR**, variable bitrate, lets the encoder choose each frame's bitrate by what the music needs, typically aiming at a constant quality setting. Simple passages get low bitrates, complex ones high bitrates. The file's size is not known in advance, and since time and bytes no longer grow together, players need help to show the duration and to seek: a [Xing or VBRI header](xing-header) in the first frame.

**ABR**, average bitrate, is a mode of LAME and other encoders: the bitrate varies like VBR but is steered toward a target average, so the file's size is predictable.

**What you see.** IsVbr in the statistics says whether the frames use more than one bitrate, from the frames themselves. A VBR header alone does not make a file VBR: LAME writes an "Info" header into CBR files too. The LAME tag's VBR Method says which mode the encoder was set to, and the bitrate distribution shows how many frames use each bitrate.

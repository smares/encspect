---
id: musepack-sv7-stream
title: Musepack SV7: header and frames
related: musepack-sv4-to-sv6, musepack-subband-coding, musepack-sv8-packets, gapless-playback, replaygain, encoder-detection
references: libmpcdec r495 streaminfo.c, mpc_demux.c, mpc_decoder.c; libmpcdec 1.2.6 streaminfo.c; mppenc 1.16 encode_sv7.c, mppenc.c; ffmpeg 6.1 libavformat/mpc.c
---

**Words.** Stream version 7, the format of mppenc up to 1.16, is one bitstream of 32-bit little-endian words, each read from its most significant bit. A field's bits therefore lie in a word's bytes from the last to the first, and a field that crosses words is split across non-adjacent bytes.

**The header.** "MP+" and a byte whose low four bits are the stream version 7 (mppenc writes 0x17 when it used noise substitution) start the stream. Six words follow: the frame count; the mid/side flag, the max band, the profile and the sample rate; ReplayGain as hundredths of a dB and linear peaks; the true gapless flag with the number of samples in the last frame; and one byte with the encoder's version, 116 for mppenc 1.16; builds before 1.06 write 0.

**Frames.** The frames start 200 bits in. Each is a 20-bit length and that many bits of data, with no sync word, so readers follow the lengths, and one damaged length loses everything after it. After the last frame the header counts, 11 bits repeat its sample count. With true gapless and more than 671 samples in that frame, libmpcdec decodes one more frame to finish the filter's output, and mppenc writes it.

**Samples.** A decoder drops the filter's 481 samples at the start and, with true gapless, the end of the last frame, so it outputs exactly the input. Without it, 481 samples are cut off the end.

**Versions 4 to 6**, older still, have no magic number at all (see their own article).

**What you see.** The header field by field, each frame with its length and subbands, and the stream's end against the frame count.

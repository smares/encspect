---
id: musepack-sv8-packets
title: Musepack SV8: packets, seek table and stream end
related: musepack-subband-coding, musepack-sv7-stream, crc, replaygain, ape-tag, encoder-detection
references: Musepack SV8 specification (mutagen-specs mirror, mpc/sv8.rst); libmpcdec r495 mpc_demux.c, mpc_bits_reader.c; libmpcenc r495 bitstream.c; ffmpeg 6.1 libavformat/mpc8.c
---

**Why packets.** SV8 keeps SV7's coding but wraps it in packets, so a file can carry new kinds of data and decoders can skip what they do not know. A stream starts with the magic number "MPCK".

**A packet** is a two-letter key from A to Z, a size and a payload. The size counts the whole packet, key and size field included, in 1 to 9 bytes of 7 bits each, the high bit set on every byte but the last. Unused bits in a packet must be zero.

**The keys.** SH, the stream header, gives the sample count, the beginning silence, the sample rate, the channels, whether mid/side is used and the frames per audio packet, protected by a CRC-32. RG holds ReplayGain as loudness and peak in dB. EI names the profile and the encoder's version. SO points to the seek table. AP, the audio packets, hold the frames, 64 per packet by default, without a length of their own, so only decoding finds where one ends. ST, the seek table, lists the offset of every 2^n-th audio packet from the magic number, the first two as sizes and the rest as Golomb codes of the change from the step before. CT packets carry chapters with an APEv2 tag each. SE, 3 bytes, ends the stream; tags such as APEv2 may only follow it.

**Damage.** libmpcdec refuses a stream header whose CRC fails and stops at a key outside A to Z. A missing stream end leaves where the stream ends unknown.

**What you see.** Every packet with its key and size, the header fields and CRC verdict, the seek table checked against the audio packets, and the samples the header announces.

---
id: wma-voice-superframes
title: Windows Media Audio Voice packets and superframes
related: wma-voice, wma-voice-frames, wma-bit-reservoir, asf-packets
references: FFmpeg 6.1.1 libavcodec/wmavoice.c (parse_packet_header, wmavoice_decode_packet, synth_superframe)
---

**The problem.** A superframe of speech takes a varying number of bits, while the container carries packets of one size, the block alignment.

**How a packet is laid out.** A packet starts with a 4-bit sequence number, a flag for residual LSPs, the number of superframes that begin in it, and the spillover: how many bits after the header finish the superframe that began in the packet before. The count is a sum of 6-bit values, each 63 saying that another follows; the spillover field is 3 bits wider than the base-2 logarithm of the block alignment. Whole superframes then follow one another bit by bit. The last of the counted superframes goes on to the end of the packet, and the next packet's spillover bits finish it. At the end of the stream a decoder reads the last one from the last packet's bits alone.

**What a superframe holds.** A speech bit, an optional sample count of at most 480, the LSPs of all three frames if the packet sets the residual flag, three frames and optional additional data, which a decoder skips. Nothing states a superframe's size: it follows from reading it.

**What you see in a file.** For each packet its header fields, the end of the superframe before, the superframes it holds and the start of the one that goes on; for each superframe its header and frames. Bits that a superframe's reading leaves unused, a superframe that needs more bits than it was given, and spillover with no start to finish are findings. After an invalid superframe in a packet, FFmpeg's decoder drops the rest of the media object, not only the rest of the packet; it cuts a media object into blocks from the end.

**In Encspect's test files.** In the two files from the Windows Media Format SDK's writer (see the codec's article), each superframe uses exactly the bits the packets give it, apart from the end: 874 bits after the 8 kHz file's last superframe, and 4,705 unread bits in the 16 kHz file's last packet. The analyzer does not rely on this.

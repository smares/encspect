---
id: monkeys-audio-descriptor-md5
title: The Monkey's Audio descriptor MD5
related: monkeys-audio-file, monkeys-audio-frames, verification, crc
references: Monkey's Audio SDK 13.42, Source/MACLib/MACLib.cpp (VerifyFileW2) and APEHeader.cpp; MACLib.h (APE_DESCRIPTOR)
---

**The problem.** A CRC in each frame shows that the audio of that frame decoded right. It does not show that every frame is there, nor that the header, seek table and WAV data around them are unchanged.

**How it works.** The descriptor holds an MD5 of the file's coded parts, taken in this order: the WAV header data, the frame data and the terminating data, the 24-byte header, and the seek table. It covers bytes as they are in the file, not decoded audio, so checking it needs no decoding. The descriptor itself is left out.

**What you see.** MD5 and Computed MD5 under the descriptor, and IsMd5Valid. A value that differs means some of the covered bytes changed; it cannot tell which. The analyzer compares the MD5 in every analysis, and a file cut short, or whose sizes in the descriptor are wrong, leaves it uncompared. Together with the frame CRCs, a matching MD5 over a complete file is what makes the stream [verified](verification). Files before version 3.98 have no descriptor and no MD5.

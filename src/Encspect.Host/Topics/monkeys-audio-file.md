---
id: monkeys-audio-file
title: The Monkey's Audio file
related: monkeys-audio-seek-table, monkeys-audio-frames, monkeys-audio-descriptor-md5, monkeys-audio-prediction, verification, bitrate-modes, channel-layouts
references: Monkey's Audio SDK 13.42, Source/MACLib/MACLib.h ("APE File Format Overview", APE_DESCRIPTOR, APE_HEADER) and APEHeader.cpp; the files of Encspect's test files written by mac 13.42
---

**The problem.** A lossless WAV file is large, and a player must still be able to rebuild it bit for bit, including the parts that are no audio: the WAV header and anything that follows the samples.

**How it works.** A Monkey's Audio file (.ape) holds the audio compressed in frames and keeps the rest of the WAV file beside them. Files of version 3.98 and later start with a 52-byte descriptor (the signature "MAC " or "MACF" for floating point, the file version, the program version that wrote it, the sizes of every part and an [MD5](monkeys-audio-descriptor-md5)), then a 24-byte header with the compression level, the format flags, the samples in a frame, the number of frames, the bits per sample, the channels and the sample rate. After them come the [seek table](monkeys-audio-seek-table), the WAV header data, the [frames](monkeys-audio-frames) and the terminating data, the bytes that followed the samples in the WAV file. Older files start with one header and have no descriptor.

**What you see.** A Descriptor and a Header node, the seek table, the frames, and any WAV chunks after the audio. The compression level runs from 1000 (fast) to 5000 (insane); a higher level runs more [prediction](monkeys-audio-prediction) and takes longer to encode and to decode. In Encspect's test files, mac 13.42 wrote file version 3.99 at every level, with 73,728 samples in a frame at the fast, normal and high levels, 294,912 at extra high and 1,179,648 at insane.

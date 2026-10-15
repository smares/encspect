---
id: dts-core-frames
title: DTS core frames
related: dts-audio-coding-header, dts-bitstream-forms, dts-core-extensions, dts-extension-substream, frame-sync, crc
references: ETSI TS 102 114 V1.6.1 (2019-08), 5.2 to 5.7; FFmpeg 6.1.1 libavcodec/dca_core.c
---

**The problem.** A DTS stream is a run of frames that a decoder has to pick up at any point, such as after a damaged stretch, and every frame has to tell it how to decode the rest.

**How it works.** A core frame starts with the 32-bit sync word 0x7FFE8001 and a frame header: whether the frame is normal or a termination frame, the number of blocks of 32 samples (NBLKS), the frame's size in bytes (FSIZE), the channel arrangement (AMODE), the sampling frequency, the targeted bit rate and flags for the optional parts. The primary audio coding header ([audio coding header](dts-audio-coding-header)) follows. The audio itself is coded in up to 16 subframes that can each be decoded without the others. Each holds the side information, the high-frequency vector quantization addresses, the LFE channel, and up to four subsubframes of subband samples, 32 subbands from a filter bank. A DSYNC word, 0xFFFF, may end each one, so a decoder notices bit errors. Optional information and the [core extensions](dts-core-extensions) come last. A normal frame holds 256, 512, 1,024, 2,048 or 4,096 samples per channel.

**Encoder revision.** The header also states a revision of the encoder software, shown as Encoder Revision (VERNUM). In Encspect's test files FFmpeg 6.1.1's dca encoder (`-strict -2`) wrote 7 in every frame, with 16 blocks (512 samples) per core frame and no extensions. The analyzer shows the field and does not use it to name an encoder.

**What you see.** Every frame with its header fields, as a stream of Frame nodes. Because a sync word can occur in audio data, the analyzer counts one only when two more frames follow at the distances the sizes give ([frame sync](frame-sync)), and the form of the bytes decides how the frame is read ([forms](dts-bitstream-forms)). The subframes are not decoded: sizes, sync words and CRCs are what the headers give.

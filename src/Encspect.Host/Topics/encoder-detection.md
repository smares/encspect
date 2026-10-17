---
id: encoder-detection
title: Encoder detection and confidence
related: writer-detection, wavpack-blocks, wavpack-sub-blocks, lame-tag, vbri-header, ancillary-data, id3v2, aac-raw-data-blocks, usac, sbr, vorbis-comments, musepack-sv8-packets, asf-metadata
references: mp3guessenc; EncSpot; LAME tag specification (Gabriel Bouvigne); lib/info.c of libvorbis and aoTuV
---

**Why it is possible.** The standards define only decoding; encoders choose freely and leave traces. The encoder turned the audio into the coded stream; the [writer](writer-detection), resolved apart, put it into the file.

**The evidence, from strongest to weakest:**

- A header naming the encoder: the [LAME tag](lame-tag) (high where its CRC holds, else medium) or Fraunhofer's [VBRI header](vbri-header), which has no CRC and is taken to name Fraunhofer (high).
- A signature in the [ancillary data](ancillary-data), such as LAME's version text, or an extension some encoders write (mp3PRO, MP3 Surround).
- Habits (low): in Encspect's test files Shine 3.1.1 never used the bit reservoir or short blocks; a habit counts only over 20 or more frames.
- In AAC, ffmpeg's or FAAC 1.x's version text in a [fill element](aac-raw-data-blocks) (high); exhale's [xHE-AAC](usac) layout and FAAC 2.x's [SBR](sbr) layout (medium). Apple's afconvert (macOS 15.7) writes no text; in Encspect's test files its MP4 files hold an sbtd box (the PCM source of an encode), which with stream type byte 0x14 is taken for Apple's encoder (medium).
- The [comment](vorbis-comments)'s vendor string in Vorbis, Opus and Speex; the version in the stream in Musepack ([SV8 packets](musepack-sv8-packets)).
- In WavPack, a new config block or a block checksum, which libwavpack has written since 5.0 ([sub-blocks](wavpack-sub-blocks)) (medium). In WMA, ffmpeg's 'N' filler bytes (high for two or more padded superframes, medium for one) and, in Encspect's Windows Media Audio 9.2 files, codec data of 17 frames per block with the bit reservoir, taken for Microsoft's (medium).
- An [ID3v2](id3v2) TSSE frame, DTS-HD's BUILDVER chunk and ffmpeg's libavcodec ID (`Lavc60.31.102 libvorbis`), low to medium: whatever wrote the tag filled it in.

**Not the encoder.** DTS' VERNUM is a revision field, and ffmpeg's muxer version names the writer. A writer of the same name only completes the version of an encoder already named ("FAAC" becomes "FAAC 2.2.0 (6acbe23)").

**Confidence.** High: the encoder signed the file in a way that checks out. Medium: specific but weaker evidence. Low: habits, or text other software may have written. A claim that names another encoder than the stream is a finding.

**What you see.** An Encoder node with its Confidence and Evidence, at the bytes that hold it; for WMA it covers the stream and the evidence gives the offset.

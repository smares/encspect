---
id: encoder-detection
title: Encoder detection and confidence
related: writer-detection, wavpack-blocks, lame-tag, vbri-header, ancillary-data, id3v2, aac-raw-data-blocks, usac, sbr, vorbis-comments, musepack-sv8-packets, asf-metadata
references: mp3guessenc; EncSpot; LAME tag specification (Gabriel Bouvigne); lib/info.c of libvorbis and aoTuV
---

**Why it is possible.** The standards define only decoding; encoders choose freely and leave traces. The encoder is the software that turned the audio into the coded stream. The software that put that stream into the file is the [writer](writer-detection), resolved apart: a remuxed file keeps its audio's encoder and gets a new writer.

**The evidence, from strongest to weakest:**

- A header with the encoder's name, such as the [LAME tag](lame-tag) or Fraunhofer's [VBRI header](vbri-header); where its CRC holds, confidence is high.
- A signature in the [ancillary data](ancillary-data), such as LAME's version text, or an extension only some encoders write (mp3PRO, MP3 Surround).
- Habits (low): in Encspect's test files Shine 3.1.1 never used the bit reservoir or short blocks. Only habits every encode shows over 20 or more frames count.
- In AAC, ffmpeg's or FAAC 1.x's version text in a [fill element](aac-raw-data-blocks) (high); exhale's [xHE-AAC](usac) layout and the [SBR](sbr) layout of FAAC 2.x's HE-AAC (medium). Apple's encoder writes no text; an MP4 track with an sbtd box, which records the PCM source of an encode, names it (medium).
- The [comment](vorbis-comments)'s vendor string in Vorbis, Opus and Speex; the encoder's version in the stream in Musepack ([SV8 packets](musepack-sv8-packets)).
- In WavPack, libwavpack 5.0's [config block](wavpack-blocks) and checksums (medium); in WMA, Microsoft's coding patterns (medium) and ffmpeg's 'N' filler bytes (high).
- An [ID3v2](id3v2) TSSE frame, DTS-HD's BUILDVER chunk and ffmpeg's libavcodec ID (`Lavc60.31.102 libvorbis`), low to medium: the tag names are filled in by whatever wrote them.

**Not the encoder.** DTS' VERNUM is a revision field, and ffmpeg's muxer version names the writer. A [writer](writer-detection) of the same name only completes the version of an encoder the stream already named ("FAAC" becomes "FAAC 2.2.0 (6acbe23)"); it never creates an encoder or raises its confidence.

**Confidence.** High means the encoder signed the file in a way that checks out; medium, specific but weaker evidence; low, only habits or text other software may have written. A claim that names another encoder than the stream is a finding.

**What you see.** An Encoder node with its Confidence and each piece of Evidence, at its bytes.

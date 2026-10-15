---
id: encoder-detection
title: Encoder detection and confidence
related: writer-detection, wavpack-blocks, lame-tag, vbri-header, ancillary-data, id3v2, aac-raw-data-blocks, usac, sbr, vorbis-comments, musepack-sv8-packets, asf-metadata
references: mp3guessenc; EncSpot; LAME tag specification (Gabriel Bouvigne); lib/info.c of libvorbis and aoTuV
---

**Why it is possible.** The standards define only decoding; encoders choose freely and leave traces: a header with their name, text in unused bits, or coding habits. The encoder is the software that turned the audio into the coded stream. The software that put that stream into the file is the [writer](writer-detection), resolved apart: a remuxed file keeps its audio's encoder and gets a new writer.

**The evidence, from strongest to weakest:**

- A header the encoder wrote with its name, such as the [LAME tag](lame-tag) or Fraunhofer's [VBRI header](vbri-header); where its CRC holds, confidence is high.
- A signature in the [ancillary data](ancillary-data), such as LAME's version text, or an extension only some encoders write (mp3PRO, MP3 Surround).
- Habits (low): in Encspect's test files Shine 3.1.1 never used the bit reservoir or short blocks, and Xing's tompg 3.0 never switched to short blocks. Only habits every encode shows over 20 or more frames count.
- In AAC, ffmpeg's or FAAC 1.x's version text in a [fill element](aac-raw-data-blocks) (high); exhale's [xHE-AAC](usac) layout and the [SBR](sbr) layout of FAAC 2.x's HE-AAC (medium). Apple's encoder writes no text; an MP4 track with an sbtd box, which records the PCM source of an encode, names it (medium).
- In Vorbis, the [comment](vorbis-comments)'s vendor string, which each libvorbis, aoTuV and Lancer release writes; in Opus, the vendor string's libopus version; in Speex, the header's version; in Musepack, the encoder's version in the stream ([SV8 packets](musepack-sv8-packets)).
- In WavPack, libwavpack 5.0's [config block](wavpack-blocks) and checksums (medium); in WMA, Microsoft's coding patterns (medium) and ffmpeg's 'N' filler bytes (high).
- An [ID3v2](id3v2) TSSE frame: low confidence alone, and the only evidence for AC-3 and DTS. The specification defines it as the used audio encoder and its settings.

**Not the encoder.** DTS' VERNUM is a revision field, and ffmpeg's muxer version names the writer.

**Confidence.** High means the encoder signed the file in a way that checks out; medium, specific but weaker evidence; low, only habits or text other software may have written. Evidence that names another encoder than the stream points to is reported as a finding on the tag.

**What you see.** An Encoder node with its Confidence and each piece of Evidence, at its bytes.

---
id: encoder-detection
title: Encoder detection and confidence
related: wavpack-blocks, lame-tag, vbri-header, ancillary-data, id3v2, aac-raw-data-blocks, usac, sbr, itunes-metadata, vorbis-comments, musepack-sv8-packets, asf-metadata
references: mp3guessenc; EncSpot; LAME tag specification (Gabriel Bouvigne); lib/info.c of libvorbis and aoTuV
---

**Why it is possible.** The standards define only decoding; encoders choose freely and many leave traces: a tag with their name, text in unused bits, or coding habits.

**The evidence, from strongest to weakest:**

- A header the encoder wrote with its name, such as the [LAME tag](lame-tag) or Fraunhofer's [VBRI header](vbri-header); where its CRC holds, confidence is high.
- A signature in the [ancillary data](ancillary-data), such as LAME's version text, or an extension only some encoders write (mp3PRO, MP3 Surround).
- Habits: Shine never uses the bit reservoir or short blocks, Xing's encoder never switches to short blocks, Fraunhofer's encoders never use scfsi. Only habits every encode shows, over at least 20 frames, count (low).
- In AAC, ffmpeg's or FAAC 1.x's version text in a [fill element](aac-raw-data-blocks) (high); exhale's [xHE-AAC](usac) layout and the [SBR](sbr) layout of FAAC 2.x's HE-AAC (medium), which other encoders' files lack. Apple's encoder writes no text; its file writer's layout names it in MP4 with an sbtd box (medium), and the writer, "Apple AudioToolbox", without one and in ADTS (low), since the writer also lays it around ADTS packets it copied. A version text in the stream wins.
- In Vorbis, the [comment](vorbis-comments)'s vendor string: each libvorbis, aoTuV and Lancer release writes its own.
- In Musepack, the encoder's version in the stream ([SV8 packets](musepack-sv8-packets)).
- In DTS, VERNUM is a revision field and names no encoder.
- In WavPack, libwavpack 5.0's [config block](wavpack-blocks) and checksums (medium).
- In ASF, [metadata](asf-metadata) such as WMFSDKVersion and the header's layout tell Microsoft's writers apart (medium); in WMA, ffmpeg's 'N' filler bytes count high.
- An ID3v2 TSSE frame, an APE Encoder item or an MP4 [©too item](itunes-metadata), which whatever software wrote the tag fills in. Alone, it names an encoder with low confidence; for AC-3 and DTS it is the only evidence.

**Confidence.** High means the encoder signed the file in a way that checks out; medium, specific but weaker evidence; low, only habits or text other software may have written.

**What you see.** An Encoder node with its Confidence and each piece of Evidence, at its bytes.

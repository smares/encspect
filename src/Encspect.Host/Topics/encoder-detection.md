---
id: encoder-detection
title: Encoder detection and confidence
related: lame-tag, vbri-header, ancillary-data, id3v2, aac-raw-data-blocks, usac, itunes-metadata, vorbis-comments, musepack-sv8-packets, asf-metadata
references: mp3guessenc; EncSpot; LAME tag specification (Gabriel Bouvigne); lib/info.c of libvorbis and aoTuV
---

**Why it is possible.** The standards define only decoding. Encoders choose freely within them, and many leave traces: a tag with their name, text in unused bits, or coding habits.

**The evidence, from strongest to weakest:**

- A header the encoder wrote with its name, such as the [LAME tag](lame-tag) or Fraunhofer's [VBRI header](vbri-header); where its CRC holds, the confidence is high.
- A signature in the [ancillary data](ancillary-data), such as LAME's version text, or an extension only certain encoders write, such as mp3PRO or MP3 Surround.
- Habits: Shine never uses the bit reservoir or short blocks, Xing's encoder never switches to short blocks, Fraunhofer's encoders never use scfsi. Only habits every encode shows, over at least 20 frames, count, with low confidence, since a new encoder may share them.
- In AAC, ffmpeg's or FAAC's version text in a [fill element](aac-raw-data-blocks), which gives high confidence; in [xHE-AAC](usac), exhale's element layout, which gives medium.
- In Vorbis, the [comment](vorbis-comments)'s vendor string: each libvorbis, aoTuV and Lancer release writes its own, which its source or builds confirm.
- In Musepack, the encoder's version in the stream ([SV8 packets](musepack-sv8-packets)).
- In ASF, [metadata](asf-metadata) such as WMFSDKVersion and the header's layout tell Microsoft's writers apart (medium); in WMA, ffmpeg's 'N' filler bytes count high.
- An ID3v2 TSSE frame or an MP4 [©too item](itunes-metadata), which whatever software wrote the tag fills in. Alone, it names an encoder with low confidence; for AC-3, whose frames name none, it is the only evidence.

**Confidence.** High means the encoder signed the file in a way that checks out; medium means specific but weaker evidence; low means only habits or text other software may have written. 

**What you see.** An Encoder node with its Confidence and each piece of Evidence, at its bytes.

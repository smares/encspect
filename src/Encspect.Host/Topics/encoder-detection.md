---
id: encoder-detection
title: Encoder detection and confidence
related: lame-tag, vbri-header, ancillary-data, id3v2, aac-raw-data-blocks, itunes-metadata
references: mp3guessenc; EncSpot; LAME tag specification (Gabriel Bouvigne)
---

**Why it is possible.** The MPEG standards define only what a decoder must do. Every encoder makes its own choices within them, and many leave traces: a tag with their name, text in unused bits, or habits in how they code.

**The evidence, from strongest to weakest:**

- A header the encoder wrote with its name, such as the [LAME tag](lame-tag) or Fraunhofer's [VBRI header](vbri-header). Where its CRC holds, the confidence is high.
- A signature in the [ancillary data](ancillary-data), such as LAME's version text, or an extension only certain encoders write, such as mp3PRO or MP3 Surround.
- Habits: Shine never uses the bit reservoir or short blocks, Xing's encoder never switches to short blocks, Fraunhofer's encoders never use scfsi, and so on. Only habits that encodes of known signals show every time, over at least 20 frames, are used, and they give low confidence, since a new encoder may share them.
- In AAC, ffmpeg's version text in a [fill element](aac-raw-data-blocks) at the start of an access unit, which gives high confidence.
- An ID3v2 TSSE frame or an MP4 [©too item](itunes-metadata), which whatever software wrote the tag fills in. It supports other evidence but alone names an encoder with low confidence. AC-3 frames carry no encoder name, so for them it is the only evidence.

**Confidence.** High means the encoder signed the file in a way that checks out; medium means specific but weaker evidence; low means only habits or text other software may have written. Silence gives an encoder nothing to decide, so a quiet file may match another encoder's habits.

**What you see.** An Encoder node in each region with its Confidence and every piece of Evidence, each pointing at the bytes it was found in.

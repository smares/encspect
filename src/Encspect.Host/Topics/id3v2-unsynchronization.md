---
id: id3v2-unsynchronization
title: ID3v2 unsynchronization
related: id3v2, frame-sync
references: ID3v2.3.0 section 5; ID3v2.4.0 section 6.1
---

**The problem.** MPEG audio decoders find frames by searching for a sync word, a byte 0xFF followed by a byte whose top three bits are set. An ID3v2 tag sits before the audio and may contain those same bytes, in a picture or a size field. A player from the 1990s that knew nothing of ID3v2 would take such bytes for a frame and play noise.

**How it works.** Unsynchronization inserts a zero byte after every 0xFF that is followed by a byte that could complete a false sync (or by a zero byte), so the tag never contains the pattern. A reader that knows ID3v2 removes the inserted zeros again. The tag header's flag says the tag was unsynchronized; ID3v2.4 applies it frame by frame instead, with a flag in each frame. For the same reason ID3v2 stores the tag's size, and in ID3v2.4 the frame sizes too, as syncsafe integers, using only seven bits of each byte, so a size never contains 0xFF.

**Today.** Players that know nothing of ID3v2 are long gone, so few taggers unsynchronize. Readers must still support it.

**What you see.** The IsUnsynchronized flags, the tag's content with the extra bytes removed, each field still pointing at its bytes in the file, and a warning where a tag marked as unsynchronized still holds a false sync.

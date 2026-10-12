---
id: ogg-pages
title: Ogg pages, packets and logical streams
related: granule-position, vorbis-comments, speex-modes, tag-placement
references: RFC 3533, sections 4 to 6
---

**The problem.** A codec produces packets of any size. A file or a network stream must carry them so that a reader can tell several streams apart, notice damage and find its place again. Ogg does this without knowing the codec: it carries packets and position markers.

**Pages and lacing.** Ogg cuts each packet into segments of 255 bytes and a final shorter one, and puts the segments into pages of at most 65,307 bytes. A page starts with the capture pattern "OggS" and a 27-byte header, followed by the segment table: one lacing value per segment. A value of 255 means the packet goes on; a smaller one ends it, so a packet of exactly 255 bytes ends with a 0. A packet that does not fit continues on the stream's next page, whose header marks it as continued.

**Damage and sync.** Each page carries a CRC-32 over the whole page, computed with the CRC field set to zero, and a sequence number that counts the pages of its stream. A reader detects damaged and lost pages from them, and after damage looks for the next "OggS" whose page passes the check.

**Logical streams.** Each page names its logical stream by a serial number. Grouping interleaves several streams, such as video and audio, and puts all their beginning-of-stream pages first; chaining joins complete groups one after another, as when tracks are concatenated. The first packet of each stream names its codec, and its last page is marked as the end.

**What you see.** Each page with its header fields and the computed CRC, the bytes of each packet on the page, and each logical stream with its codec, pages and packets.

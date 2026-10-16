---
id: itunes-and-audible-protection
title: iTunes and Audible protection in MP4
related: common-encryption, itunes-metadata, iso-base-media-format
references: MediaInfo File_Mpeg4_Elements.cpp (drms); VLC modules/demux/mp4/libmp4.c (drms, drmi and their atoms); GStreamer gst/isomp4/qtdemux.c (aavd, drms, drmi); FFmpeg libavformat/mov.c (mov_read_adrm)
---

**Two schemes of their own.** Apple's iTunes Store protected its downloads in a scheme of its own, and Audible protects its audiobooks in a scheme of its own. Both are MP4 files whose protected track has a sample entry other than the usual one and no Common Encryption boxes.

**iTunes Store.** The audio sample entry is `drms` (`drmi` for video). Its layout is that of the original entry, with an `esds` box and a `sinf` box inside. The `sinf` box names the original format in `frma`, and its scheme information holds atoms such as `user`, `key `, `iviv` and `priv`. VLC lists these as the DRM atoms of a `drms` entry and skips them, and MediaInfo reports the track's encryption as iTunes. No source the analyzer follows describes what is inside them, so they are shown by size only.

**Audible.** The entry is `aavd`, laid out like `mp4a` (GStreamer's `qtdemux` reads it so), with an `esds` box for the original AAC and an `adrm` box. FFmpeg's reader takes 88 bytes from `adrm`: 8 bytes it skips, a 56-byte blob it decrypts with a key derived from the account's activation bytes, 4 more bytes and a 20-byte checksum.

**Not protection.** An iTunes purchase also writes the buyer's account (`apID`), the purchase date (`purd`) and the owner (`ownr`) into the file's metadata. These items are tags. A file with them but an ordinary `mp4a` entry is not protected.

**What you see.** The stream is marked protected with the scheme iTunes protection or Audible, and encrypted. The analyzer neither decrypts nor reads these samples, and describes the stream from the `esds` box.

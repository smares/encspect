---
id: common-encryption
title: Common Encryption in MP4
related: iso-base-media-format, fragmented-mp4, mp4-sample-tables
references: ISO/IEC 23001-7 (Common encryption in ISO base media file format files); ISO/IEC 14496-12 8.12 (protected tracks); MP4 registration authority (scheme types and boxes); DASH-IF content protection identifiers; FFmpeg libavformat/mov.c
---

**What problem it solves.** A streaming service wants to encrypt a file once and let phones, browsers and set-top boxes with different DRM systems play it. Common Encryption separates the two jobs: the file says how its samples are encrypted, and each DRM system supplies the keys in its own way.

**How the file says it.** The sample entry of a protected track is renamed (`enca` for audio, `encv` for video). Its `sinf` box names the original format in `frma`, the scheme in `schm` and the scheme's data in `schi`. The four schemes are `cenc` (AES-CTR), `cens` (AES-CTR on a pattern of blocks), `cbc1` (AES-CBC) and `cbcs` (AES-CBC on a pattern). A pattern encrypts a number of 16-byte blocks, skips a number, and repeats.

**Track defaults.** The `tenc` box gives the default key ID (KID), whether samples are protected, the size of the per-sample initialization vector (IV) and, where there is none, a constant IV. A sample group of type `seig` can override the defaults for some samples, which is how a clear lead-in is marked.

**Per-sample data.** Each sample's IV and the clear and encrypted ranges inside it (subsamples) are in a `senc` box or in auxiliary information that `saiz` and `saio` size and locate.

**DRM systems.** A `pssh` box holds one system's data for getting the keys, named by a 16-byte system ID. DASH-IF keeps a public registry of them, such as Widevine and Microsoft PlayReady. The keys are never in the file.

**What you see.** The `sinf` and `tenc` fields, one node per `pssh`, and the stream marked as protected with its scheme and systems. The analyzer does not decrypt. It describes an encrypted stream from its sample entry and sample tables and reads no frames, because ciphertext is not valid audio; a track whose default says not protected and has no protecting sample group is read as it is.

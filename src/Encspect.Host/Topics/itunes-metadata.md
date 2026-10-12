---
id: itunes-metadata
title: iTunes metadata in MP4
related: iso-base-media-format, edit-lists-and-priming, encoder-detection, id3v2
references: QuickTime File Format, Metadata atoms and types, Well-known types; ISO/IEC 14496-12 8.11.1; TagLib
---

**Where it lives.** MP4 has no tag of its own in the ISO standard, only a generic `meta` box. Apple's iTunes defined what has become the usual one: a `meta` box in `moov`'s user data, whose handler `mdir` says it holds an item list (`ilst`). Every item is a box whose four-character type names the field, such as `©nam` for the title, `©ART` for the artist, `trkn` for the track number or `covr` for cover art.

**Data atoms.** Each item holds one or more `data` atoms with a type, a locale and the value. The type comes from QuickTime's well-known types: 1 is UTF-8 text, 13 and 14 are JPEG and PNG images, 21 a signed integer, and 0 binary data whose layout the item implies, such as `trkn`'s pair of 16-bit numbers for the track and the number of tracks.

**Freeform items.** A field without a code of its own uses a freeform item (`----`) named by a domain, `com.apple.iTunes` for Apple's, and a name. Apple stores its gapless information as iTunSMPB, see [edit lists and priming](edit-lists-and-priming), and Sound Check values as iTunNORM.

**Other forms.** QuickTime metadata uses the handler `mdta` and a `keys` box with reverse-DNS names such as com.apple.quicktime.title. An `ID32` box can hold a whole [ID3v2](id3v2) tag. Encoders often write their name into `©too`, the encoding tool, which other software may also set, so the analyzer takes it as a claim of low confidence in [encoder detection](encoder-detection).

**What you see.** An iTunes Metadata tag node with each item named for its field, its data atoms and their values. The item values are cross-checked against TagLib#, ffmpeg and MP4Box.

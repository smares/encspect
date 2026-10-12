---
id: vorbis-comments
title: Vorbis comments
related: encoder-detection, tag-placement, replaygain, ogg-pages
references: Vorbis I specification, section 5; Speex manual, section 7.3
---

**What it is.** The Vorbis comment is the tag of the Xiph.Org formats. Vorbis made it its second header packet, and Speex, Opus and FLAC took the same format: Speex and Opus as a header packet of their own in Ogg, FLAC as a metadata block. Each codec puts its own framing around it, such as Vorbis' packet type and framing bit or Opus' "OpusTags", and Speex none at all.

**How it works.** A vendor string comes first, naming the library or program that wrote the stream. A count of comments follows, then the comments, each preceded by its length as a 32-bit little-endian integer. Each comment is UTF-8 text of the form NAME=value, much like an environment variable. The field name is ASCII from 0x20 to 0x7D without the '=', and case does not matter.

**No fixed list.** The specification proposes a minimal set of field names, such as TITLE, ARTIST, ALBUM, TRACKNUMBER, DATE and GENRE, and makes none of them mandatory. Any other name may be used, and a name may appear more than once, as ARTIST does for a piece by several artists. Names such as ALBUMARTIST, ENCODER and REPLAYGAIN_TRACK_GAIN have grown up beside the list, so what such a field means is a matter of practice, not of the format. The comment is meant for short text notes; the specification puts larger metadata into a stream of its own.

**What you see.** The Vorbis Comment tag with its vendor string and each comment, named after its field, with its length, field name and value. The vendor string and an ENCODER field are evidence for the encoder.

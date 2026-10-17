---
id: quicktime-user-data
title: QuickTime user data text
related: itunes-metadata, iso-base-media-format, writer-detection
references: QuickTime File Format, User data atoms, Language code values, Digital audio file formats; ffmpeg libavformat/mov.c (mov_read_udta_string); MediaInfoLib File_Mpeg4_Elements.cpp (moov_udta_xxxx)
---

**Where it lives.** A `udta` box in `moov` holds data about the whole movie, and one in `trak` data about a track. Its children are atoms whose four-character type says what they hold. QuickTime defines a set of text atoms for it, such as `©nam` for the title, `©cpy` for the copyright, `©day` for the creation date and `©swr` for the software that generated the movie. Every type that begins with the © character (ASCII 169) is international text.

**Text items.** An international text atom holds a list of text items, so one atom can carry a title in several languages. Each item is a 16-bit size, a 16-bit language code and the text. ffmpeg and MediaInfo take the size as the length of the text alone, which is how the analyzer reads it; the File Format words it as including the size and language fields.

**Language and encoding.** A language code below 0x400, or 0x7FFF (an unspecified language), is a Macintosh language code (0 is English), and the text is Macintosh-encoded; ffmpeg reads it as Macintosh Roman, as the analyzer does. Any other code is three letters of ISO 639-2/T packed in 5 bits each, and the text is UTF-8, or UTF-16 after a byte order mark.

**What you see.** A `udta` box that holds a text atom is the file's tag, "QuickTime User Data", with each text atom named for its field and one text item per language. Besides the atoms the File Format lists, `©ART`, `©alb`, `©cmt` and `©des` (named for the MP3 importer's metadata in its appendix on digital audio files) and `©too` (which ffmpeg's and MediaInfo's readers take as the encoding tool) are read this way. An atom whose item sizes do not fit its bytes is reported (`mp4.text-size-invalid`, `mp4.text-item-truncated`), and the text that is there is shown. `©swr` and `©too` count as low-confidence claims about the writer, see [writer detection](writer-detection); the iTunes metadata, which uses `ilst` items instead, is described in [iTunes metadata](itunes-metadata).

# Vorbis comments

- The comment format that Speex, Vorbis, Opus and FLAC share: a vendor string and a list of `NAME=value` comments.
- The codec parsers hand their comment packets to this parser: [Speex](../formats/speex.md), [Vorbis](../formats/vorbis.md) and [Opus](../formats/opus.md).
- The vendor string is how those codecs' encoders are named.

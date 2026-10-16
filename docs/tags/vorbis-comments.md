# Vorbis comments

- The comment format that Speex, Vorbis, Opus and FLAC share: a vendor string and a list of `NAME=value` comments.
- The codec parsers hand their comment packets to this parser: [Speex](../formats/speex.md), [Vorbis](../formats/vorbis.md) and [Opus](../formats/opus.md).
- The vendor string is how those codecs' encoders are named (a claim of the encoder role), unless it is ffmpeg's muxer version ("Lavf60.16.100"), which names the writer.
- An `ENCODER` field is a claim of the writer role: section 5.2.2 of the Vorbis I specification lists no such field, and RFC 7845 section 5.2 has Opus applications use it "to identify themselves". ffmpeg's libavcodec encoder ID in it ("Lavc60.31.102 libvorbis") names the encoder.

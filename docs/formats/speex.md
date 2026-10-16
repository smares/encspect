# Speex

Speex in [Ogg](ogg.md).

## What is read

- the header;
- the submodes of the frames in every packet: narrowband, wideband and ultra-wideband layers, and in-band requests;
- the granule positions;
- the comments, through the [Vorbis comment](../tags/vorbis-comments.md) parser.

## Reported

- the delay, end trimming and duration;
- bitrates and packet sizes;
- the frames of each submode;
- the bitrate of every packet and per second.

## Channel layout

Mono or stereo.

## Encoder detection

- **Encoder:** speexenc, from its vendor string ("Encoded with Speex 1.2.1") with the header's version agreeing (high), or libspeex from the header's version string, which libspeex writes whatever program encodes (low).
- **Encoder, too:** ffmpeg, from an ENCODER field that is its libavcodec ID ("Lavc60.31.102 libspeex"), with medium confidence (bit-exact, without a version: "Lavc libspeex", shown "FFmpeg (Lavc libspeex)"). A vendor string that names speexenc wins over it. An ID of another codec's encoder never becomes the encoder, stays as evidence under it, as does an own ID that a vendor string outranks (every ENCODER field is weighed, not the first), and is `speex.encoder-other-format`. A bit-exact ffmpeg writes "ffmpeg" as the vendor string, which names the writer "FFmpeg".
- **Writer:** a vendor string that is ffmpeg's muxer version ("Lavf60.16.100") and any other ENCODER field. Two claims that name different software are `speex.writer-claim-differs`, and software known to write another format is `speex.writer-other-format`.

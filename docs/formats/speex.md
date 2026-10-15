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
- **Writer:** ffmpeg, from the ENCODER field ("Lavc60.31.102 libspeex") and a vendor string that is its muxer's version ("Lavf60.16.100"). Two claims that name different software are `speex.writer-claim-differs`.

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

speexenc, ffmpeg's libspeex, or the libspeex version.

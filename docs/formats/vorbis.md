# Vorbis

Vorbis in [Ogg](ogg.md).

## What is read

- **Identification header:** with its bitrate hints and channel layout.
- **Comment header:** through the [Vorbis comment](../tags/vorbis-comments.md) parser.
- **Setup header:** field by field: codebooks, floors, residues, mappings with their square polar coupling steps, and modes.
- **Audio packets:** each packet's mode and block size.

## Reported

- long and short blocks;
- the samples the overlap of blocks gives, checked against the granule positions with their start offset and trimming;
- the bitrate of every packet and per second, and packet sizes.

## Channel layout

From the channel count, in the order of the Vorbis I specification, section 4.3.9.

## Encoder detection

From the vendor string:

- libvorbis by release;
- aoTuV by release, from its sources and builds;
- Lancer;
- ffmpeg.

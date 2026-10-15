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

- **Encoder**, from the vendor string:
  - libvorbis by release;
  - aoTuV by release, from its sources and builds;
  - Lancer.
- **Encoder, too:** an ENCODER field that is ffmpeg's libavcodec ID ("Lavc60.31.102 libvorbis"), shown as "FFmpeg (Lavc60.31.102 libvorbis)" with medium confidence (bit-exact, without a version: "FFmpeg (Lavc libvorbis)"). One that names another codec's encoder is kept and is `vorbis.encoder-other-format`.
- **Writer:** any other ENCODER field, which section 5.2.2 of the Vorbis I specification does not list, and a vendor string that is ffmpeg's muxer version ("Lavf60.16.100"): ffmpeg rewrites the vendor string, so its Ogg Vorbis files name the writer "FFmpeg (Lavf60.16.100)". Two claims that name different software are `vorbis.writer-claim-differs`, and software known to write another format is `vorbis.writer-other-format`.

## Not yet covered

- Floors and residues (planned: entropy pass).

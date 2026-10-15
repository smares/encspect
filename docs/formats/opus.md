# Opus

Opus in [Ogg](ogg.md) (RFC 6716, RFC 7845).

## What is read

- **OpusHead:** with every channel mapping family: mono and stereo, the Vorbis order up to 7.1, ambisonics in families 2 and 3, and family 255.
- **OpusTags:** through the [Vorbis comment](../tags/vorbis-comments.md) parser.
- **Packets:** every packet's TOC byte and framing, multistream packets included, checked against the rules of RFC 6716.

## Reported

- the pre-skip and end trimming, at 48 kHz;
- frames per mode, bandwidth and frame size;
- stereo packets and frame packing;
- whether every packet has the same size, as CBR writes them;
- the bitrates.

## Channel layout

From mapping family 0 or 1. Ambisonics and family 255 name no speakers.

## Encoder detection

- **Encoder:** the vendor string of libopusenc, which names libopus' version ("libopus 1.4"), with medium confidence.
- **Writer:** the ENCODER field, which the Vorbis comment specification leaves open: opusenc's "opusenc from opus-tools 0.2" is shown as "opusenc 0.2", ffmpeg's "Lavc60.31.102 libopus" as "FFmpeg (Lavc60.31.102 libopus)"; a vendor string that is ffmpeg's muxer version ("Lavf60.16.100") names the writer too. Two claims that name different software are `opus.writer-claim-differs`.

## Not yet covered

- The SILK and CELT layers (planned: entropy pass).

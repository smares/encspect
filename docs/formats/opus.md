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
- **Encoder, too:** an ENCODER field that is ffmpeg's libavcodec ID ("Lavc60.31.102 libopus"), shown as "FFmpeg (Lavc60.31.102 libopus)" with medium confidence (bit-exact, without a version: "Lavc libopus", shown "FFmpeg (Lavc libopus)"). A vendor string that names libopus wins over it; an ID of another codec's encoder never becomes the encoder, stays as evidence under it and is `opus.encoder-other-format`.
- **Writer:** the ENCODER field of an application (RFC 7845 section 5.2: the vendor string is "intended to identify the codec encoder and encapsulation implementations", and "user-facing applications can use the 'ENCODER' user comment tag to identify themselves"): opusenc's "opusenc from opus-tools 0.2" is shown as "opusenc 0.2". A vendor string that is ffmpeg's muxer version ("Lavf60.16.100") names the writer too. Two claims that name different software are `opus.writer-claim-differs`, and software known to write another format is `opus.writer-other-format`.

## Not yet covered

- The SILK and CELT layers (planned: entropy pass).

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

## Not covered

- The coded data of the frames (the LSPs, pitch, gains and excitation): only the submodes, the in-band requests and the lengths they imply are read; no audio is rebuilt.
- In-band requests are skipped by their sizes (the standard handlers' and the user handler's); what they ask is counted, not applied, except that the stereo parameters are counted.
- Bits after the frames the header gives per packet: decoders never read them, so they are shown as ignored data and are no finding (only a complete extra frame is `speex.extra-frames`).
- A wideband layer before a frame of a narrowband stream is skipped as libspeex 1.2.1 skips it (submodes 1 to 4); submode 0 and 5 to 7 make that decoder read the layer again and stop as corrupt, which is `speex.frame-invalid`.

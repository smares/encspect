# Speex

Speex in [Ogg](ogg.md).

## What is read

- the header (speex_header.h), every field, with findings for:
  - a size other than 80, an undefined mode, a frame size that is not the mode's;
  - a bit-stream version other than 4 (speexdec refuses it), channels other than 1 or 2, a sample rate of 0 or less, frames per packet outside 1 to 10;
  - a version ID other than 1 (a warning above 1, since speexdec refuses it), and reserved fields that are not 0;
  - extra headers are shown;
- the frames of every packet: the submodes of the narrowband, wideband and ultra-wideband layers, in-band requests, and the parameters of each layer (the entropy pass, in every analysis):
  - narrowband: the line spectral pair indexes, the open-loop pitch, the pitch coefficient of the forced-pitch submodes, the global gain, the pitch and pitch gain index of each subframe, the gain correction and the codebook entries of the innovation (two codebooks in submode 7);
  - wideband and ultra-wideband layers: the two line spectral pair indexes, and for each subframe the gain and the codebook entries with their signs (two codebooks in submode 4);
  - the intensity stereo requests (balance and energy ratio);
  - the bits each layer's parameters take are exactly those its submode fixes (libspeex's `bits_per_frame`), which `SpeexSyntaxTests` holds for every submode on random data;
- the granule positions:
  - speexenc and ffmpeg write the samples coded up to a page's last packet less the encoder's lookahead (40, 143 and 349 samples in narrowband, wideband and ultra-wideband); the last page's may be smaller, where the input ended;
  - the first position gives the delay (libspeex's lookahead is assumed where the last page holds the only one), every later one must agree with it, and the last gives the end trimming;
  - the samples are the coded samples less both, which equals the last granule position, and a gap re-anchors;
  - speexdec writes a different count where its trimming, which uses the decoder's lookahead, does not cancel out; the cross-check models it;
- the samples of each packet, from the header's frames per packet and the frame size: an empty packet, a packet whose frames break the syntax and a packet the container cut count them and are counted again as Lost Samples; a packet with fewer frames than the header gives, and no fault, counts the frames it holds;
- the sizes and bitrates cover the packets the container hands over whole; a cut packet counts in the samples only;
- the comments, through the [Vorbis comment](../tags/vorbis-comments.md) parser.

## Reported

- **Bitrate Mode** (the mode the stream or its container states where it states one (the nominal bitrates of AC-3 and of DTS without extension substreams, 0x7FF in ADTS, the variable flag of ASF), else variable or constant by the units' own bitrates, unknown for one unit) and **Bitrate Distribution** (the units' bitrates counted in ranges of 32 kbps, with each range's share), beside the minimum and maximum bitrate. The header's vbr flag states a variable mode when it is 1; a clear flag states nothing (speexenc and ffmpeg leave it unset in ABR), so the packets' own bitrates decide. The stream's `isVbr` (JSON, the GUI's VBR) follows the same decision, so the stream and the statistic agree; there is no separate `IsVbr` statistic.
- the delay, end trimming and duration;
- bitrates and packet sizes;
- the frames of each submode;
- the bitrate of every packet and per second.

## Channel layout

Mono or stereo: two channels are intensity-coded stereo, which decodes to left and right.

## Memory

- Speex packets are small (70 bytes at 28 kbps), so what is kept per packet matters as much as the audio.
- A packet keeps an 8-byte record (its length and first frame), 4 bytes per frame and its two values of the per-packet bitrate series, whose x values are the packets' offsets, so the records keep no offset of their own: 30 bytes in all, plus its share of the 56-byte page records.
- The packets' bytes and `MappedBytes` are not kept once the codec has read them.
- Measured on a 196 MB file of 2.74 million packets: the report keeps 81 MB and the heap peaks at 91 MB; the process peaks at 285 MB, of which the collector's allocation budget is about 110 MB on the test machine (174 MB with `DOTNET_GCgen0size` at 16 MB); the CSV takes 3.5 s.
- A 202 MB MP3 of 482,000 frames peaks at 247 MB on the same machine.
- Storing the per-packet series as 32-bit values would save 8 more bytes per packet, but needs `Series` to accept them, a contract change.

## Encoder detection

- **Encoder:** speexenc, from its vendor string ("Encoded with Speex 1.2.1") with the header's version agreeing (high), or libspeex from the header's version string, which libspeex writes whatever program encodes (low).
- **Encoder, too:** ffmpeg, from an ENCODER field that is its libavcodec ID ("Lavc60.31.102 libspeex"), with medium confidence (bit-exact, without a version: "Lavc libspeex", shown "FFmpeg (Lavc libspeex)"). A vendor string that names speexenc wins over it. An ID of another codec's encoder never becomes the encoder, stays as evidence under it, as does an own ID that a vendor string outranks (every ENCODER field is weighed, not the first), and is `speex.encoder-other-format`. A bit-exact ffmpeg writes "ffmpeg" as the vendor string, which names the writer "FFmpeg".
- **Writer:** a vendor string that is ffmpeg's muxer version ("Lavf60.16.100") and any other ENCODER field. Two claims that name different software are `speex.writer-claim-differs`, and software known to write another format is `speex.writer-other-format`.

## Not covered

- The parameters are read, not reported: the report has no figure of them (no graph of pitch or gains), and they reach the decoder only. The survey in [coding-graphs.md](../design/coding-graphs.md#speex) holds a row for each with its milestone.
- In-band requests other than the stereo parameters are skipped by their sizes (the standard handlers' and the user handler's); what they ask is counted, not applied. libspeex applies them only where an application registers a handler, and speexdec registers the stereo one only.
- Bits after the frames the header gives per packet: decoders never read them, so they are shown as ignored data and are no finding (only a complete extra frame is `speex.extra-frames`).
- A wideband layer before a frame of a narrowband stream is skipped as libspeex 1.2.1 skips it (submodes 1 to 4); submode 0 and 5 to 7 make that decoder read the layer again and report a corrupt stream, which is `speex.frame-invalid`; only the layers it skips count for `speex.wideband-layer-in-narrowband`.

## Decoding

- **Scope:** narrowband, wideband and ultra-wideband, mono and in-band stereo, as libspeex 1.2.1's fixed-point decoder (`--enable-fixed-point`) decodes them, with the perceptual enhancer on, as speexdec decodes: bit for bit its 16-bit output ([the cross-check](../development.md#speex-decoding)). Codec ID `speex`; the stream stores no checksum, so the decoder runs where `--measure` or the library's output sink asks for the samples.
- **A stream the decoder refuses:** a mode other than 0 to 2, a bit-stream version other than 4 or a version ID above 1 (speexdec refuses them too), or a sample rate of 0 or less; the verification says it cannot be decoded.
- **The parser reads the coded bits:** the entropy pass lends the decoder the parameters of every frame (`SpeexLayout`: 200 integers a frame) and the decoder reads no coded bit. A packet is walked a second time, into the lent buffers, when it is handed to the decoder.
- **Output:** 16-bit integers, one or two channels. The stereo decoder expands the mono frame by the last stereo request's balance and energy ratio with the smoothing of libspeex's `speex_decode_stereo_int`. A header that names a channel count above 1 is decoded as stereo, as speexdec does (a count of 0 or less reads as 1, as libspeex's `speex_packet_to_header` clamps it). A stereo request with no frame after it in its packet (a crafted stream; speexenc writes one before every frame) is dropped with the packet, where libspeex's handler carries the balance into the next packet: the one place the output may differ from libspeex's.
- **Comfort noise:** submode 0 frames and the discontinuous transmission of the sub-band layers use libspeex's generator (`speex_rand`, seed 1000 per decoder) in the order libspeex draws from it, and restart at the seed after a gap.
- **Trims:** the granule positions give the range `[delay, delay + Samples)` of the coded stage (the delay and the end trimming above): the first packets wait for the first granule position (at most 512 packets or 1 MiB of them, then a delay of 0 is assumed, which is what the report's samples count from when no position comes; where nothing is decoded no packet's bytes are held at all) and the last four for the end of the stream, so that the trims reach them before they are decoded.
- **Gaps and withheld output:** a packet the walk cannot read, an empty one and one the container cut are lost, and the packet after them follows a gap. The decoder then starts over as at the beginning of the stream (libspeex conceals a lost packet with `nb_decode_lost`; the analyzer decodes nothing it did not read) and withholds the first 20 frames of 20 ms (the reason "after a gap"). 20 frames come from the fixtures: a decoder started cold at every packet of the speech fixtures is within 16 least significant bits of one that went on from the 21st frame in 9 of 10 cuts, and from the 25th in 99 of 100 ([design](../design/lossy-decoders.md#as-built-speex)); it is an estimate, as the design says of every span of recursive state.
- **Not decoded:** a frame libspeex's arithmetic cannot decode (a division by zero in the sub-band layer's filter ratio, which crashes libspeex; the enhancer's and the stereo expansion's divisors cannot reach zero) is `decode.frame-invalid`, and the decoder starts over at the next packet.

## Articles

- **Articles:** `speex-modes`, `speex-excitation`, `line-spectral-pairs`, `speex-enhancer`.
- **Shared with other formats:** `vorbis-comments`, `encoder-detection`, `bitrate-modes`, `granule-position`.

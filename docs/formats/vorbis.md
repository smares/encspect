# Vorbis

Vorbis in [Ogg](ogg.md).

## What is read

- **Identification header** (Vorbis I specification, section 4.2.2): every field, the bitrate hint the three bitrate fields give together, and the channel layout of section 4.3.9 for one to eight channels.
  - Findings for what libvorbis 1.3.7 refuses (`_vorbis_unpack_info`): a version other than 0, no channels, a sample rate of 0, block sizes outside 64 to 8192 or a short one larger than the long one, no framing bit.
  - The comment header's "\x03vorbis" prefix and the framing bit after the comment are read too, and so are the page layout and header granule positions of section A.2.
- **Comment header:** through the [Vorbis comment](../tags/vorbis-comments.md) parser.
- **Setup header:** field by field: codebooks, floors, residues, mappings with their square polar coupling steps, and modes.
  - It is walked as a decoder unpacks it (section 4.2.4), least significant bit first, without building Huffman trees or lookup tables; every field is a node at its bits, and each group's value gives its counts and key parameters.
  - Codebooks: the sync pattern, dimensions, entries, ordered and sparse flags, codeword lengths (summarized as used entries and their range) and lookup (minimum, delta, value bits, sequence flag and the multiplicands as one node).
  - Also: the time domain placeholders, each floor of type 0 or 1 with its parameters, classes and X list, each residue with its range, partition size, classifications, cascade and books, each mapping with its submaps, coupling steps, channel mux, floors and residues, and each mode.
  - Findings for what breaks the specification: a sync pattern, lookup, floor, residue or mapping type it does not define; references to codebooks, floors, residues or mappings that do not exist; coupling steps that name one channel twice or one beyond the stream; reserved fields; window or transform types other than 0; no framing bit.
  - Findings for what libvorbis 1.3.7 refuses beyond it, from its sources: `ilog(dimensions) + ilog(entries)` above 24; codeword lengths that do not form a complete prefix code, except a single entry of length 1; more than 63 or repeated floor 1 X values; floor 0 parameters of 0 and books without lookup; residue classbooks too small for their classifications.
  - A structure that cannot be followed (an undefined type, the end of the packet) stops the walk; what was read stays in the tree.
- **Audio packets** (section 4.3.1): the packet type bit, the mode number in `ilog(modes - 1)` bits, which gives the block size, and a long block's previous and next window flags. Each packet keeps an 8-byte record (length, mode, flags).
  - Findings: window flags that contradict the neighbors' sizes, empty packets, packets with the type bit set, undefined or missing modes, granule positions that do not match the packets or go beyond them.
  - The first audio page's granule position may start the stream later (an info) or discard samples at the start, at most what the first two blocks return, which libvorbisfile also discards no more than (oggdec confirms it); the second audio packet must then end its page.
- **Memory:** a packet keeps its 8-byte record and its two values of the per-packet bitrate series, 26 bytes per packet with its share of the page records on linux-x64. A 278 MB file of 245,000 packets (libvorbis at quality 10) gives its CSV in 1.9 s at a peak of 80 MB, and its whole JSON report in 3.3 s at 202 MB.
- **Samples:** each block counts the samples its overlap with the previous block returns (the first block none); Gap Trimming, like the start and end trimming, takes samples off the trimmed stage only. A packet the container cut whose mode still reads counts its block and is counted again as Lost Samples; a cut packet whose mode does not read declares nothing.
- **Bitrates:** the sizes and bitrates cover the packets the container hands over whole; a cut packet counts in the samples only.

## Reported

- **Bitrate Mode** (the mode the stream or its container states where it states one (the nominal bitrates of AC-3 and of DTS without extension substreams, 0x7FF in ADTS, the variable flag of ASF), else variable or constant by the units' own bitrates, unknown for one unit) and **Bitrate Distribution** (the units' bitrates counted in ranges of 32 kbps, with each range's share), beside the minimum and maximum bitrate. The identification header's three bitrate hints state the mode only when all three are set and equal (constant) or when a maximum and/or a minimum is set (variable, as section 4.2.2 reads it); a nominal alone states nothing, so the packets' own bitrates decide (a long block and a short block of the same size differ in bitrate). The stream's `isVbr` (JSON, the GUI's VBR) follows the same decision, so the stream and the statistic agree; there is no separate `IsVbr` statistic.
- **Block Sizes**, the packets that code a block as a tally and a category series (long and short, with the series' time index of the seconds), named as MPEG audio and AAC name the grouping of their block types (it replaces the "Long Blocks" and "Short Blocks" nodes);
- the samples the overlap of blocks gives, checked against the granule positions with their start offset and trimming (the start trimming is capped at what the packet that carries the first position returns and the end trimming at what the last packet returns, as libvorbis does; one larger than the first two blocks return, or the last packet, is a finding);
- the count of samples the granule positions are checked against follows libvorbis's `vorbis_synthesis_blockin` (libvorbis 1.3.7; `oggdec` from vorbis-tools 1.4.2 and 1.4.3 write the same figures on the fixtures named here):
  - it continues from the last granule position taken, so the samples of a repeated packet, or a jump of the positions in the middle, are not charged to the end trimming; each position that differs from the count continued from the position before it is counted in `vorbis.granule-inconsistent` (not each position that differs from the first page's);
  - a position counts only on a packet that codes a block: one on an empty, not-audio or invalid packet is not taken, and the packet after such a packet, or after a gap, starts a new count, counted from the first block after it; the first position taken is measured by that count (so a refused packet before it can leave no start trimming), and a later one below the samples counted since discards the difference at that page, at most the last packet's samples (`Gap Trimming`);
  - every end-of-stream page trims, at most the last packet's samples (more is `vorbis.end-trimming-large`; a trim the count does not reach is `vorbis.granule-beyond-packets`), and its position becomes the count, as every position does, so a repeated end-of-stream page keeps the first page's trim and trims nothing more, and a page that follows an end-of-stream page in sequence is measured from it;
  - the count is held as the last position and the samples decoded then, and the offsets are clamped, so a position of any size cannot overflow; a figure at the 64-bit limit is shown as "at least";
- the bitrate of every packet and per second, and packet sizes;
- the samples before trimming as "Coded Samples" (the name Speex, Musepack, TrueHD and standard WMA use for the same stage).

## Channel layout

From the channel count, in the order of the Vorbis I specification, section 4.3.9, with the same speaker and layout names as Opus and the other formats.

## Encoder detection

- **Encoder**, from the vendor string:
  - libvorbis by release: it writes its vendor string into every comment header it encodes, and the tags of its repository give each release's (1.0 to 1.3.7; 1.1.1 and 1.1.2 write the same, 1.2.1 has no tag), which name "libvorbis 1.3.7" with high confidence;
  - aoTuV by release, with high confidence: lib/info.c of each source release on aoTuV's site (beta 1a to beta 6.03 and its 2014 and 2015 rebuilds), the 2018 and 2020 rebuilds ("… (based on libvorbis 1.3.6)" and "… 1.3.7)") and the reference binaries of a1 to a3 and b1, whose sources are gone, give them; beta 6.03 of 2011, 2014 and 2015 write the same "AO; aoTuV [20110424] (based on Xiph.Org's libVorbis)"; `aotuv_tool.py --vendors` checks the table against the sources;
  - Rarewares' oggenc2.88 on aoTuV b6.03 (2020) writes "AO; aoTuV 6.03 (2020) (based on libVorbis 1.3.7)" and its Lancer SSE2 build "BS; LancerMod(SSE2) (based on aoTuV 6.03 (2020))", both confirmed by fixtures they wrote under Wine;
  - other strings of these forms (Lancer's SSE3 build, whose binary holds its own) name aoTuV or Lancer with medium confidence, and other libvorbis-form strings name libvorbis with their date, with medium confidence;
  - setup headers do not tell aoTuV from libvorbis where a vendor string was rewritten: at quality 10 aoTuV b5.7, b6.03 and libvorbis 1.3.7 write the same setup header, and at quality 5 b5.7 writes libvorbis 1.3.7's, so no habit is used;
  - oggenc writes no ENCODER field.
- **Encoder, too:** an ENCODER field that is ffmpeg's libavcodec ID ("Lavc60.31.102 libvorbis"), shown as "FFmpeg (Lavc60.31.102 libvorbis)" with medium confidence (bit-exact, without a version: "Lavc libvorbis", shown "FFmpeg (Lavc libvorbis)"). A vendor string that names libvorbis wins over it; an ID of another codec's encoder never becomes the encoder, stays as evidence under it, as does an own ID that a vendor string outranks (every ENCODER field is weighed, not the first), and is `vorbis.encoder-other-format`.
- **Writer:** any other ENCODER field, which section 5.2.2 of the Vorbis I specification does not list, and a vendor string that is ffmpeg's muxer version ("Lavf60.16.100"): ffmpeg rewrites the vendor string, so its Ogg Vorbis files name the writer "FFmpeg (Lavf60.16.100)". Two claims that name different software are `vorbis.writer-claim-differs`, and software known to write another format is `vorbis.writer-other-format`.
- **In a container:** a stream whose headers a container holds apart (a Matroska track's CodecPrivate) weighs the container's claims about the file first (the Segment's WritingApp and MuxingApp, after the file's tags) and then its own comment's. The container's writer is the stream's, and a comment that names other software is `vorbis.writer-claim-differs` against it (a copied Ogg file's vendor "Lavf60.16.100" in a file mkvmerge wrote).

## Reference for sample counts

- The reference for Vorbis sample counts is Xiph's decoder (libvorbis fed packet by packet; `oggdec`), run by `.github/workflows/official-decode.yml` and held to the analyzer by `OggOfficialDecodersTests`. How it is driven, what the reference rule is and the differences found are described under "Reference for sample counts" in [ogg.md](ogg.md).
- Start and end trimming are compared with `oggdec`'s output, the packets' samples (the overlap of the blocks) with libvorbis's, and libvorbis's own granule handling is a third witness.
- The analyzer's end trimming follows libvorbis's granule handling (run 41457775152). A repeated packet in the middle of a file, which put the analyzer 38 to 128 samples below `oggdec` in 10 of the 14 derived Vorbis files, and a repeated end-of-stream page (384 or 986 samples above `oggdec` in 4 of them), now equal `oggdec` in all 14.
- The Vorbis I specification (A.2) says only that the last page's position may be below what the final packet returns; it does not say from which earlier position the count runs, or what a decoder does with a position in the middle that disagrees. The analyzer follows the reference implementation there (`vorbis_synthesis_blockin` takes each position it is given and sets its count to it), and `OggFixtureTests` holds the figures `oggdec` writes for `vorbis-packet-repeated`, `vorbis-granule-midstream`, `vorbis-granule-extreme`, `vorbis-empty-carrier` and `vorbis-end-page-repeated`.

## Not yet covered

- Coded bandwidth (Hz, as MPEG audio, AAC, AC-3 and Opus report it): not covered yet. Vorbis states no bandwidth field; where coding stops lies in the floor and residue data of the audio packets, which needs the frame-level parsing that comes with the format's decoder.
- The floor and residue data of the audio packets (planned: entropy pass). The setup header's floor and residue configurations are read.

## Articles

- **Articles:** `vorbis-blocks`, `vorbis-setup-header`, `square-polar-coupling`.
- **Shared with other formats:** `vorbis-comments`, `granule-position`, `gapless-playback`, `mid-side-stereo`, `replaygain`.

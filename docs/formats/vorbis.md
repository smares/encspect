# Vorbis

Vorbis in [Ogg](ogg.md).

## What is read

- **Identification header:** with its bitrate hints and channel layout.
- **Comment header:** through the [Vorbis comment](../tags/vorbis-comments.md) parser.
- **Setup header:** field by field: codebooks, floors, residues, mappings with their square polar coupling steps, and modes.
- **Audio packets:** each packet's mode and block size.
- **Samples:** each block counts the samples its overlap with the previous block returns (the first block none); Gap Trimming, like the start and end trimming, takes samples off the trimmed stage only. A packet the container cut whose mode still reads counts its block and is counted again as Lost Samples; a cut packet whose mode does not read declares nothing.

## Reported

- long and short blocks;
- the samples the overlap of blocks gives, checked against the granule positions with their start offset and trimming (the start trimming is capped at what the packet that carries the first position returns and the end trimming at what the last packet returns, as libvorbis does; one larger than the first two blocks return, or the last packet, is a finding);
- the count of samples the granule positions are checked against follows libvorbis's `vorbis_synthesis_blockin` (libvorbis 1.3.7; `oggdec` from vorbis-tools 1.4.2 and 1.4.3 write the same figures on the fixtures named here):
  - it continues from the last granule position taken, so the samples of a repeated packet, or a jump of the positions in the middle, are not charged to the end trimming; each position that differs from the count continued from the position before it is counted in `vorbis.granule-inconsistent` (not each position that differs from the first page's);
  - a position counts only on a packet that codes a block: one on an empty, not-audio or invalid packet is not taken, and the packet after such a packet, or after a gap, starts a new count, counted from the first block after it; the first position taken is measured by that count (so a refused packet before it can leave no start trimming), and a later one below the samples counted since discards the difference at that page, at most the last packet's samples (`Gap Trimming`);
  - every end-of-stream page trims, at most the last packet's samples (more is `vorbis.end-trimming-large`; a trim the count does not reach is `vorbis.granule-beyond-packets`), and its position becomes the count, as every position does, so a repeated end-of-stream page keeps the first page's trim and trims nothing more, and a page that follows an end-of-stream page in sequence is measured from it;
  - the count is held as the last position and the samples decoded then, and the offsets are clamped, so a position of any size cannot overflow; a figure at the 64-bit limit is shown as "at least";
- the bitrate of every packet and per second, and packet sizes.

## Channel layout

From the channel count, in the order of the Vorbis I specification, section 4.3.9, with the same speaker and layout names as Opus and the other formats.

## Encoder detection

- **Encoder**, from the vendor string:
  - libvorbis by release;
  - aoTuV by release, from its sources and builds;
  - Lancer.
- **Encoder, too:** an ENCODER field that is ffmpeg's libavcodec ID ("Lavc60.31.102 libvorbis"), shown as "FFmpeg (Lavc60.31.102 libvorbis)" with medium confidence (bit-exact, without a version: "Lavc libvorbis", shown "FFmpeg (Lavc libvorbis)"). A vendor string that names libvorbis wins over it; an ID of another codec's encoder never becomes the encoder, stays as evidence under it, as does an own ID that a vendor string outranks (every ENCODER field is weighed, not the first), and is `vorbis.encoder-other-format`.
- **Writer:** any other ENCODER field, which section 5.2.2 of the Vorbis I specification does not list, and a vendor string that is ffmpeg's muxer version ("Lavf60.16.100"): ffmpeg rewrites the vendor string, so its Ogg Vorbis files name the writer "FFmpeg (Lavf60.16.100)". Two claims that name different software are `vorbis.writer-claim-differs`, and software known to write another format is `vorbis.writer-other-format`.
- **In a container:** a stream whose headers a container holds apart (a Matroska track's CodecPrivate) weighs the container's claims about the file first (the Segment's WritingApp and MuxingApp, after the file's tags) and then its own comment's. The container's writer is the stream's, and a comment that names other software is `vorbis.writer-claim-differs` against it (a copied Ogg file's vendor "Lavf60.16.100" in a file mkvmerge wrote).

## Reference for sample counts

- The reference for Vorbis sample counts is Xiph's decoder (libvorbis fed packet by packet; `oggdec`), run by `.github/workflows/official-decode.yml` and held to the analyzer by `OggOfficialDecodersTests`. How it is driven, what the reference rule is and the differences found are described under "Reference for sample counts" in [ogg.md](ogg.md).
- Start and end trimming are compared with `oggdec`'s output, the packets' samples (the overlap of the blocks) with libvorbis's, and libvorbis's own granule handling is a third witness.
- The analyzer's end trimming follows libvorbis's granule handling (run 41457775152). A repeated packet in the middle of a file, which put the analyzer 38 to 128 samples below `oggdec` in 10 of the 14 derived Vorbis files, and a repeated end-of-stream page (384 or 986 samples above `oggdec` in 4 of them), now equal `oggdec` in all 14.
- The Vorbis I specification (A.2) says only that the last page's position may be below what the final packet returns; it does not say from which earlier position the count runs, or what a decoder does with a position in the middle that disagrees. The analyzer follows the reference implementation there (`vorbis_synthesis_blockin` takes each position it is given and sets its count to it), and `OggFixtureTests` holds the figures `oggdec` writes for `vorbis-packet-repeated`, `vorbis-granule-midstream`, `vorbis-granule-extreme`, `vorbis-empty-carrier` and `vorbis-end-page-repeated`.

## Not yet covered

- The floor and residue data of the audio packets (planned: entropy pass). The setup header's floor and residue configurations are read.

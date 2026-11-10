# Opus

Opus in [Ogg](ogg.md) (RFC 6716, RFC 7845).

## What is read

- **OpusHead:** with every channel mapping family: mono and stereo, the Vorbis order up to 7.1, ambisonics in families 2 and 3, and family 255.
  - Fields (RFC 7845 section 5.1): version, channels, pre-skip, input sample rate, output gain in 1/256 dB, the mapping family and, for families other than 0, the stream and coupled counts and the channel mapping table.
  - The input sample rate is information only: Opus always decodes at 48 kHz, so the stream's rate is 48 kHz and the header keeps the input's apart.
  - The table gives each output channel with the stream it comes from and its place: a speaker of family 1 (Vorbis order, up to 7.1), an ambisonic channel number or the non-diegetic pair of family 2 (RFC 8486), or silence.
    - Family 3 replaces it with a demixing matrix of C rows by N + M columns of Q15 coefficients, reported by its dimensions.
    - Families 0 and 1 also get a Channel Layout node.
  - Findings: an upper version nibble decoders do not know; 0 channels, more than two in family 0 or eight in family 1; channel counts other than (1 + n)² + 2j in families 2 and 3; 0 streams; more coupled streams than streams; more than 255 decoded channels; table entries beyond the decoded channels; a header too short for its table.
  - A reserved family is an info, since RFC 8486 says decoders should not decode it; its table is read in the format RFC 8486 recommends.
- **Header pages** (RFC 7845 section 3): the identification header alone on its page, the comment header ending its page, both on pages of granule position 0. After the comment, any data is shown with whether its first byte's least significant bit marks binary data to keep or padding.
- **OpusTags:** through the [Vorbis comment](../tags/vorbis-comments.md) parser.
- **Packets:** every packet's TOC byte and framing, multistream packets included, checked against the rules of RFC 6716.
  - Read (RFC 6716 sections 3.1 to 3.4): the TOC byte (configuration, so mode, bandwidth and frame size; stereo flag; frame count code), code 3's frame count byte with its VBR and padding flags, the padding length, the frame lengths, every frame and the padding. Packets that break R1 to R7, which decoders reject, are findings.
  - A CBR code 3 packet whose implicit frames exceed 1275 bytes breaks R2 (section 3.4). The padding length node holds the padding the length bytes code (255 adds 254 and continues), not the number of bytes they take.
  - In multistream files (families other than 0) every stream but the last uses the self-delimited framing of appendix B, which is read too. A stream's packet that does not fit, and streams of different durations (RFC 7845 section 3), are findings.
  - Each stream of a packet keeps a 24-byte record and 2 bytes per frame. The streams after one whose framing broke are implied, not recorded, so a broken multistream packet costs one record (51 bytes per packet of two bytes in the memory test, against 5 KB before).
- **Durations:** a packet's samples at 48 kHz come from its TOC (and frame count byte), as `opus_packet_get_nb_samples` counts them; granule positions count them with the pre-skip included (section 4).
  - The first audio page's position may start the stream later (an info) but not earlier, except on the end-of-stream page (section 4.5); only that page may trim the end, by no more than the last packet (section 4.4).
  - The samples are the decoded samples less the pre-skip and the end trimming, which opusdec writes.
- **Memory:** an Opus packet keeps about 40 bytes on linux-x64, with its share of the page records (the memory tests allow about twice that). A 198 MB file of 395,000 packets in two chained links gives its CSV in 1.5 s at a peak of 99 MB, and its whole JSON report in 3.9 s at 213 MB.
- **Samples:** each packet counts the samples its TOC byte and frame count byte declare (0 where they exceed 120 ms), a packet that breaks the rules, a multistream packet whose streams last different times and a packet the container cut included; Lost Samples counts those packets at this coded stage, inside the pre-skip or not.
- **Bitrates:** the sizes and bitrates (average, smallest, largest, the series) cover the packets the container hands over whole; a cut packet counts in the samples only.

## Reported

- **Bitrate Mode** (the mode the stream or its container states where it states one (the nominal bitrates of AC-3 and of DTS without extension substreams, 0x7FF in ADTS, the variable flag of ASF), else variable or constant by the units' own bitrates, unknown for one unit) and **Bitrate Distribution** (the units' bitrates counted in ranges of 32 kbps, with each range's share), beside the minimum and maximum bitrate. An Opus stream states no mode, so a packet's bits over the samples it codes decide. The stream's `isVbr` (JSON, the GUI's VBR) follows the same decision, so the stream and the statistic agree; there is no separate `IsVbr` statistic. **Channel Coding** counts the packets whose TOC sets the stereo flag, with the entries Stereo and Mono: the channel count a packet codes, not the left/right or mid/side coding that other formats call Stereo Coding.
- **End Trimming** in Matroska: the DiscardPadding of the track's last block (RFC 9559, section 5.1.3.5.7) at 48 kHz, capped at the samples the packets hold after the pre-skip; a negative DiscardPadding or one on another block is read and shown but not applied.
- the pre-skip (the header field "Pre-skip", and "Start Trimming" in the statistics, the samples a decoder drops at the start, named as for Vorbis) and end trimming, at 48 kHz;
- "Coded Samples", the samples the packets code before any trimming;
- frames per mode, bandwidth and frame size;
- **Coded Bandwidth**: the lowest, most common and highest bandwidth the frames' TOC bytes name, in whole hertz (4,000 for narrowband, 6,000, 8,000, 12,000 and 20,000 for fullband; RFC 6716, table 1), and the series "Coded Bandwidth" (Hz, per packet, the highest of the stream parts of a multistream packet; a packet that codes no samples or breaks the rules has none) and "Coded Bandwidth Over Time" (each second's highest), concept `codedBandwidth`. The "Bandwidths" tally keeps the names; the figure is the bandwidth the configuration states, not a measurement of the sound;
- stereo packets and frame packing;
- whether every packet has the same size, as CBR writes them ("all n bytes", which opusinfo's "hard-CBR" confirms);
- frames without data and padding bytes;
- the bitrates.

## Channel layout

From mapping family 0 or 1, in the Vorbis channel order, with the speakers named as everywhere else ("front left", "back left", "LFE") and the layouts by their common names ("Mono", "Stereo", "3.0", "Quadraphonic", "5.1"). Ambisonics and family 255 name no speakers.

## Encoder detection

- **Encoder:** the vendor string of libopusenc, which names libopus' version ("libopus 1.4"), with medium confidence.
- **Encoder, too:** an ENCODER field that is ffmpeg's libavcodec ID ("Lavc60.31.102 libopus"), shown as "FFmpeg (Lavc60.31.102 libopus)" with medium confidence (bit-exact, without a version: "Lavc libopus", shown "FFmpeg (Lavc libopus)"). A vendor string that names libopus wins over it; an ID of another codec's encoder never becomes the encoder, stays as evidence under it, as does an own ID that a vendor string outranks (every ENCODER field is weighed, not the first), and is `opus.encoder-other-format`.
- **Writer:** the ENCODER field of an application (RFC 7845 section 5.2: the vendor string is "intended to identify the codec encoder and encapsulation implementations", and "user-facing applications can use the 'ENCODER' user comment tag to identify themselves"): opusenc's "opusenc from opus-tools 0.2" is shown as "opusenc 0.2". A vendor string that is ffmpeg's muxer version ("Lavf60.16.100") names the writer too. Two claims that name different software are `opus.writer-claim-differs`, and software known to write another format is `opus.writer-other-format`.

## Reference for sample counts

- The reference for Opus sample counts is Xiph's decoder (libopus fed packet by packet; `opusdec --rate 48000`), run by `.github/workflows/official-decode.yml` and held to the analyzer by `OggOfficialDecodersTests`. How it is driven, what the reference rule is and the differences found are described under "Reference for sample counts" in [ogg.md](ogg.md).
- Pre-skip and end trimming are compared with `opusdec`'s output, the packets' samples with libopus's.
- The analyzer's counts have not been changed to the reference yet.

## Not yet covered

- The SILK and CELT layers (planned: entropy pass).

## Articles

- **Articles:** `opus-modes`, `opus-toc`, `opus-pre-skip`, `opus-channel-mapping`.
- **Shared with other formats:** `vorbis-comments`, `granule-position`, `gapless-playback`, `mid-side-stereo`.

# Planned formats

The formats not read yet, in the order of the [plan's milestones](../plan.md#milestones). Each gets its own page when it is built, and each is its own pull request with fixtures, cross-checks and a review; a container's routes to these codecs follow as they did for the lossless formats.

The order follows two rules: formats whose audio the existing parsers and decoders already read come first, since they cost a container or a transport and no codec; among the rest, the better documented a format is, the sooner it comes, so the most exotic come last (the lead, 2026-11-17). Where a format has no reason of its own below, its place follows from these rules alone.

## In 0.2: header level only

- **TAK and OptimFROG:** what their headers reveal. OptimFROG and TAK are done ([optimfrog.md](optimfrog.md), [tak.md](tak.md)).
- **Encoder settings:** where a header stores them (OptimFROG's mode and speedup, TAK's preset), they are read and named, once a verifiable source and files the encoders write confirm the fields.
- **OptimFROG DualStream:** an `.ofs` is named "OptimFROG" with a claim that it is the lossy part of a DualStream (low confidence from the data blocks' reserved bytes, medium when the file name ends in `.ofs`), and a correction file opened alone "OptimFROG correction data"; the pair is checked with WavPack's, as built in 0.4 ([tak-optimfrog-headers.md](../design/tak-optimfrog-headers.md#optimfrog-dualstream)).
- **Why no deeper in 0.2:** both are poorly documented. TAK got a decoder in 0.4 through a clean-room description of FFmpeg's decoder ([tak.md](tak.md)); OptimFROG has no open source for its coding, so no decoder is planned ([plan](../plan.md#not-planned)).

## After 0.5: more formats

Later milestones, in this order.

### 1. CAF

- **What:** Apple's Core Audio Format, the container of ALAC, AAC and PCM on Apple systems (`.caf`).
- **Specification:** Apple's public Core Audio Format specification.
- **Scope:** its chunks, the packet table, and routes to the codec parsers that exist.
- **Why first:** Apple's specification is public, and the committed ALAC fixtures from Apple's encoders include CAF files that wait for a CAF parser.

### 2. MPEG transport and program streams

- **What:** `.ts`, `.m2ts`, `.mpg` and `.vob` as audio containers.
- **Specification:** ITU-T H.222.0 (free): the packets and PES headers.
- **Scope:** their audio streams (MPEG audio, AAC, AC-3, E-AC-3, DTS, TrueHD, LPCM) handed to the codec parsers; video streams are named, not read.

### 3. AAC in LATM and LOAS

- **What:** the transport of broadcast AAC.
- **Specification:** ISO/IEC 14496-3, 1.7.
- **Scope:** the transport, handed to the AAC codec parser.

### 4. Sun AU, VOC, IRCAM and NIST SPHERE

- **What:** `.au` and `.snd`, Creative's `.voc`, IRCAM's `.sf`, NIST's `.sph`.
- **Scope:** small headers of their own and the encodings they name.

### 5. DSF and DSDIFF

- **What:** the DSD files of SACD rips and downloads (`.dsf`, `.dff`).
- **Specifications:** Sony's DSF specification and Philips' DSDIFF specification.
- **Scope:** headers and chunks; the DST-compressed form of DSDIFF named and measured.

### 6. AMR and AMR-WB

- **What:** the `.amr` storage format, and 3GP's `samr` and `sawb` entries in the MP4 routes.
- **Specifications:** RFC 4867 for the storage format; 3GPP TS 26.101 and 26.201 (free) for the frames.
- **Reference code** (the lead, 2026-11-23): opencore-amr (sourceforge.net/projects/opencore-amr), the AMR codecs extracted from the Android Open Source Project under the Apache License 2.0: OpenCORE's AMR-NB decoder and encoder and AMR-WB decoder, and VisualOn's AMR-WB encoder (vo-amrwbenc, whose license is read when the item starts). The decoders follow it with its notice; its encoders write fixtures.

### 7. MPEG-4 ALS

- **What:** the ALS configuration and frames.
- **Specification:** ISO/IEC 14496-3 subpart 11, and the reference software where its license allows (decision 10 in [decoders.md](../design/decoders.md)).

### 8. AC-4

- **What:** raw AC-4, and MP4's `ac-4` sample entry with `dac4` in the MP4 routes.
- **Specification:** ETSI TS 103 190-1 and -2, which are free.
- **Scope:** the TOC, presentations and substreams.
- **Decoder:** a follow-up after the parser, with a design of its own (the lead, 2026-11-16).

### 9. Dolby TrueHD as IEC 61937 MAT bursts in WAV

- **What:** the TrueHD stream that HDMI captures and FFmpeg's spdif muxer write as MAT bursts (data type 22): the MAT frames are unpacked and their access units handed to the TrueHD parser as file ranges, the pieces a MAT code cuts joined again.
- **Needs:** the walk of the multichannel high-bit-rate carriers that hold MAT bursts; the burst walker reads 16-bit stereo carriers only ([wav.md](wav.md)).
- **Sources:** no public description of the MAT frame is known; FFmpeg's spdif muxer (LGPL) is the only detailed source, so the clean-room split of item 13 applies. Fixtures come from FFmpeg's spdif muxer, run as a program.
- **Moved here** from "not planned" by the lead on 2026-11-17: the TrueHD parser and decoder now exist, and the packet routes read payloads as file ranges with their byte pairs swapped back.

### 10. MPEG-H 3D Audio

- **What:** MHAS packets and the configuration, and MP4's `mha1` and `mhm1` entries.
- **Specification:** ISO/IEC 23008-3.
- **Decoder:** a follow-up after the parser, with a design of its own (the lead, 2026-11-16); the specification is not free.

### 11. Shorten

- **What:** `.shn`.
- **Specification:** Tony Robinson's technical report (CUED/F-INFENG/TR.156).
- **Why late:** its reference code is not freely licensed, so it is not followed.
- **Fixtures:** Shorten 3.6.1 from Rarewares, whose license forbids commercial use; whether its output may be committed is checked when the item starts.

### 12. mp3HD

- **What:** Fraunhofer's lossless extension of MP3: an ordinary MP3 stream plus a lossless residual in its ID3v2 tag.
- **Scope:** naming the extension and the residual's size and position; no decoding of the residual.
- **Fixtures:** Fraunhofer's mp3HD tools 1.5.0, which have a Linux command-line build (Rarewares).

### 13. Formats whose only detailed public source is FFmpeg

- **What,** in this order:
  - OSQ, WaveLab's lossless format;
  - ATRAC, ATRAC3 and ATRAC3plus (`.oma`/`.aa3`, `.at3`, and ATRAC in WAV), with fixtures from Sony's command-line at3tool (the PSP and PS3 builds Rarewares keeps);
  - RealAudio: the RealMedia container with Cook, RealAudio 14.4 and 28.8, and RealAudio Lossless;
  - TwinVQ (`.vqf`);
  - QDesign Music, in QuickTime;
  - Nellymoser, in FLV.
- **Scope:** header and frame level.
- **How:** each is built clean-room, as TrueHD was: a format description written from FFmpeg first, then an implementation by an agent that has not read FFmpeg's code.
- **Why this late:** FFmpeg's LGPL code is the only detailed source, so each needs the clean-room split.

### 14. Game and console middleware

- **What:** ADX and HCA (CRI), BRSTM (Nintendo), VAG (Sony), FSB (FMOD) and Bink Audio.
- **Sources:** none of them has a public specification; each is reverse-engineered. A reference whose license allows it is followed with its notice, after its license is read; where FFmpeg's LGPL code is the only detailed source, the clean-room split of item 13 applies.
- **Moved here** from "not planned" by the lead on 2026-11-16: they are coded audio, so they fit the project's domain, and the plan already takes formats whose only source is reverse-engineered.

### 15. Older lossless compressors and speech codecs

- **What:** the lossless compressors and speech codecs (vocoders) no item above names. Each is named, with its sources and fixtures, when its turn comes.
- **Moved here** from "not planned" by the lead on 2026-11-16: Shorten (item 11), AMR (item 6) and WMA Voice are already planned, so the blanket exclusion no longer held.

### 16. HALAC

- **What:** `.halac`, a lossless codec (a lossy mode since 1.6.1) by Hakan Abbas, under the MIT license (github.com/Hakan-Abbas/HALAC-High-Availability-Lossless-Audio-Compression).
- **Specification:** none; the repository holds the source of 0.1.9, while the releases are at 1.6.1 (Windows x64 binaries).
- **Why late:** no stated file signature and no statement that the format is stable (the levels were renumbered in 0.5.7); it starts once the source of the current format is published or the format is stated stable.
- **Fixtures:** the release's own encoder, whose MIT license allows committing its output.

### 17. LC3 and LC3plus

- **What:** the Low Complexity Communication Codec of Bluetooth LE Audio, and LC3plus.
- **Specifications:** the Bluetooth SIG's LC3 specification and ETSI TS 103 634 (LC3plus), both free.
- **Reference code:** Google's liblc3 (github.com/google/liblc3, Apache 2.0), which implements both; the decoder follows it with its notice.
- **Fixtures:** liblc3's tools `elc3` and `dlc3`. Their files use the non-standard format of the reference encoder and decoder tools, as liblc3's README says, so the format they write is the one read first, and any other container (such as LC3 in MP4 or Matroska) when one is found.

## Not planned

- **Sequenced formats** (MIDI, tracker modules and chiptunes): dropped by the project lead on 2026-11-08, as a different domain from bitstream and encoder analysis. The scope they had is recorded in [history.md](../history.md#dropped-plans).
- The other formats that are not planned are listed in the [plan](../plan.md#not-planned).

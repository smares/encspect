# Planned formats

The formats not read yet, in the order of the [plan's milestones](../plan.md#milestones). Each gets its own page when it is built, and each is its own pull request with fixtures, cross-checks and a review; a container's routes to these codecs follow as they did for the lossless formats.

The order follows how well a format is documented: the better documented, the sooner it comes, so the most exotic come last. Where a format has no reason of its own below, its place follows from that rule alone.

## In 0.2: header level only

- **TAK and OptimFROG:** what their headers reveal. OptimFROG is done ([optimfrog.md](optimfrog.md)); TAK follows.
- **Encoder settings:** where a header stores them (OptimFROG's mode and speedup, TAK's preset), they are read and named, once a verifiable source and files the encoders write confirm the fields.
- **OptimFROG DualStream:** an `.ofs` is named "OptimFROG DualStream", lossy by itself, and a correction file opened alone "OptimFROG correction data", as WavPack's hybrid files and `.wvc` files are; the pair is checked in 0.4 with WavPack's ([tak-optimfrog-headers.md](../design/tak-optimfrog-headers.md#optimfrog-dualstream)).
- **Why no deeper:** both are poorly documented.

## After 0.5: more formats

Later milestones, in this order.

### 1. CAF

- **What:** Apple's Core Audio Format, the container of ALAC, AAC and PCM on Apple systems (`.caf`).
- **Specification:** Apple's public Core Audio Format specification.
- **Scope:** its chunks, the packet table, and routes to the codec parsers that exist.
- **Why first:** Apple's specification is public, and the committed ALAC fixtures from Apple's encoders include CAF files that wait for a CAF parser.

### 2. DSF and DSDIFF

- **What:** the DSD files of SACD rips and downloads (`.dsf`, `.dff`).
- **Specifications:** Sony's DSF specification and Philips' DSDIFF specification.
- **Scope:** headers and chunks; the DST-compressed form of DSDIFF named and measured.

### 3. Sun AU, VOC, IRCAM and NIST SPHERE

- **What:** `.au` and `.snd`, Creative's `.voc`, IRCAM's `.sf`, NIST's `.sph`.
- **Scope:** small headers of their own and the encodings they name.

### 4. AC-4

- **What:** raw AC-4, and MP4's `ac-4` sample entry with `dac4` in the MP4 routes.
- **Specification:** ETSI TS 103 190-1 and -2, which are free.
- **Scope:** the TOC, presentations and substreams.

### 5. AAC in LATM and LOAS

- **What:** the transport of broadcast AAC.
- **Specification:** ISO/IEC 14496-3, 1.7.
- **Scope:** the transport, handed to the AAC codec parser.

### 6. AMR and AMR-WB

- **What:** the `.amr` storage format, and 3GP's `samr` and `sawb` entries in the MP4 routes.
- **Specifications:** RFC 4867 for the storage format; 3GPP TS 26.101 and 26.201 (free) for the frames.

### 7. MPEG transport and program streams

- **What:** `.ts`, `.m2ts`, `.mpg` and `.vob` as audio containers.
- **Specification:** ITU-T H.222.0 (free): the packets and PES headers.
- **Scope:** their audio streams (MPEG audio, AAC, AC-3, E-AC-3, DTS, TrueHD, LPCM) handed to the codec parsers; video streams are named, not read.

### 8. MPEG-H 3D Audio

- **What:** MHAS packets and the configuration, and MP4's `mha1` and `mhm1` entries.
- **Specification:** ISO/IEC 23008-3.

### 9. MPEG-4 ALS

- **What:** the ALS configuration and frames.
- **Specification:** ISO/IEC 14496-3 subpart 11, and the reference software where its license allows (decision 10 in [decoders.md](../design/decoders.md)).

### 10. Shorten

- **What:** `.shn`.
- **Specification:** Tony Robinson's technical report (CUED/F-INFENG/TR.156).
- **Why late:** its reference code is not freely licensed, so it is not followed.

### 11. Formats whose only detailed public source is FFmpeg

- **What,** in this order:
  - OSQ, WaveLab's lossless format;
  - ATRAC, ATRAC3 and ATRAC3plus (`.oma`/`.aa3`, `.at3`, and ATRAC in WAV);
  - RealAudio: the RealMedia container with Cook, RealAudio 14.4 and 28.8, and RealAudio Lossless;
  - TwinVQ (`.vqf`);
  - QDesign Music, in QuickTime;
  - Nellymoser, in FLV.
- **Scope:** header and frame level.
- **How:** each is built clean-room, as TrueHD was: a format description written from FFmpeg first, then an implementation by an agent that has not read FFmpeg's code.
- **Why last:** FFmpeg's LGPL code is the only detailed source, so each needs the clean-room split.

## Not planned

- **Sequenced formats** (MIDI, tracker modules and chiptunes): dropped by the project lead on 2026-11-08, as a different domain from bitstream and encoder analysis. The scope they had is recorded in [history.md](../history.md#dropped-plans).
- The other formats that are not planned are listed in the [plan](../plan.md#not-planned).

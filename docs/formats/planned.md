# Planned formats

The formats of phases 4 to 6, in the order of the [plan](../PLAN.md#format-rollout). None is read yet. Each gets its own page when it is built, and each is its own pull request with fixtures, cross-checks and a review; a container's routes to these codecs follow as in phase 3.

The order follows how well a format is documented: the better its public specification, the sooner it comes, so the most exotic come last.

## Phase 4: header level only

- **TAK and OptimFROG:** what their headers reveal.
- **Why here and no deeper:** both are poorly documented, with no public specification of their coding.

## Phase 5: more formats

### 1. CAF

- **What:** Apple's Core Audio Format, the container of ALAC, AAC and PCM on Apple systems (`.caf`).
- **Specification:** Apple's public Core Audio Format specification.
- **Scope:** its chunks, the packet table, and routes to the codec parsers that exist.
- **Why first:** a public specification, and its codecs are read already; the ALAC fixtures from Apple's encoders include CAF files that wait for it.

### 2. DSF and DSDIFF

- **What:** the DSD files of SACD rips and downloads (`.dsf`, `.dff`).
- **Specifications:** Sony's DSF specification and Philips' DSDIFF specification.
- **Scope:** headers and chunks; the DST-compressed form of DSDIFF named and measured.
- **Why here:** public specifications of simple chunked files.

### 3. Sun AU, VOC, IRCAM and NIST SPHERE

- **What:** `.au` and `.snd`, Creative's `.voc`, IRCAM's `.sf`, NIST's `.sph`.
- **Scope:** small headers of their own and the encodings they name.
- **Why here:** small, documented headers.

### 4. AC-4

- **What:** raw AC-4, and MP4's `ac-4` sample entry with `dac4` in the MP4 routes.
- **Specification:** ETSI TS 103 190-1 and -2, which are free.
- **Scope:** the TOC, presentations and substreams.
- **Why here:** free specifications, but a larger syntax than the formats before it.

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
- **Why late:** its reference code is not freely licensed, so it is not followed, and the report is the only source.

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

## Phase 6: sequenced formats

These files hold instructions for making sound, not coded audio, so they are analyzed at the structure level and never rendered. The result model gains node kinds for their patterns, instruments and samples where the existing kinds do not fit.

### 1. MIDI

- **What:** Standard MIDI Files (`.mid`, formats 0 to 2) and RMID.
- **Specification:** the MIDI 1.0 specification.
- **Scope:** the header, tracks and events, tempo and time-signature maps, programs, text and lyric events, the duration worked out from the tempo map, and which extensions (General MIDI, GS, XG) the SysEx messages name.

### 2. Tracker modules

- **What:** MOD, S3M, XM, IT and OpenMPT's MPTM, then MO3 and UMX.
- **Sources:** the formats' own documentation; libopenmpt (BSD) may be followed with its notice where the documentation stops, as for MO3.
- **Scope:**
  - the header, order list, patterns, instruments and samples (bit depth, loops, compression);
  - the song length from a walk of the order list with its jumps and loops;
  - which tracker wrote the file, from the version fields and the habits OpenMPT documents.

### 3. Chiptunes

- **What:** SID (PSID and RSID), NSF, SPC, VGM and GBS.
- **Scope:** the headers and metadata; the music itself is code for an emulated chip and is not read.

# History

What was built before the plan was rewritten around milestones on 2026-11-08, with the gates passed, the dated decisions and the pull request numbers. The plan itself is in [plan.md](plan.md); what each format reads today is on its page under [formats](formats/README.md).

## Contents

- [Phases and gates](#phases-and-gates)
- [Gates in detail](#gates-in-detail)
- [Phase 1 (v1): MP3](#phase-1-v1-mp3)
- [Phase 2: the lossy formats](#phase-2-the-lossy-formats)
- [Phase 3: the lossless formats and decoders](#phase-3-the-lossless-formats-and-decoders)
- [Measurements and the GUI's graphs](#measurements-and-the-guis-graphs)
- [Phase 3, continued](#phase-3-continued)
- [Gap audits](#gap-audits)
- [Dropped plans](#dropped-plans)

## Phases and gates

MP3 came first, then the other lossy formats, then the lossless ones. Each phase started only after the one before passed its gate: the pipeline green on every platform and the independent reviews' findings resolved.

| Phase | Scope | State |
| --- | --- | --- |
| 1 (v1) | MP1/2/3 and tags | built |
| Gate 1 | Pipeline green, review findings fixed, abstractions settled | passed, October 2026 |
| 2 (lossy) | MP4 and AAC, Ogg with Speex, Vorbis and Opus, Musepack, AC-3 and E-AC-3, ASF and WMA, WavPack, DTS; then "Phase 2, continued" | built |
| Gate 2 | Pipeline green, review findings fixed | passed, 2026-10-18 at fa4e0c5 |
| 3 (lossless) | FLAC, WAV and AIFF, ALAC, WMA Lossless frames, Monkey's Audio, TTA, Dolby TrueHD and MLP; decoders for the codings that store a checksum over decoded audio | built |
| Gate 3 | Pipeline green, review findings fixed | passed, 2026-11-01 at 5f054cc |
| 3, continued | Official-decoder cross-checks (built); lossyWAV and lossyFLAC detection (paused, then made the first item of 0.2) | built, and moved |

- **Release 0.1.0:** a GitHub pre-release after Gate 2 (the project lead, 2026-10-17). It replaced the earlier rule of no public version until development ends.
- **Phases 4 to 6** were planned as TAK and OptimFROG at header level, more formats, and the sequenced formats. The milestones of the [plan](plan.md#milestones) replaced them on 2026-11-08.

## Gates in detail

- **Gate 1** (October 2026): six independent reviews found 56 problems and the fuzzer 2 more, all fixed (#26 to #31, and three host defects with the contract).
  - Areas: ID3v2; the other tags and tag detection; MPEG framing and Layers I and II; Layer III and ancillary data; VBR headers, encoder detection and statistics; the contract, host and CLI.
  - The contract review's 12 gaps closed in four steps (#32 to #35): exception boundary, cancellation and parser contexts; typed data with node kinds, streams, series and linked findings; routes from containers to codec and tag parsers; compact MPEG frame records.
  - The core model was settled here; why it is not declared stable yet is in [library.md](library.md#later-nuget-packages-for-net).
- **Gate 2** (2026-10-18, fa4e0c5): ten independent reviews, each followed by a review of its fixes and a verification.
  - Areas: ASF and WMA; WavPack; AAC; Ogg, Speex, Vorbis and Opus; AC-3 and E-AC-3; Musepack; MP4; DTS; Matroska and WebM; the host, contract and front ends.
  - Every finding was fixed in #98 to #110, one pull request per area. Also fixed: the shared record containers, QuickTime user data as tags, one stream-change rule for the raw stream formats with the APE item budget, and the host's contract and naming.
  - Release 0.1.0 followed it; Gate 3 work started after that.
- **Gate 3** (2026-11-01, 5f054cc): ten independent reviews, each followed by a review of its fixes.
  - Areas: the decoder host; FLAC; WavPack; Monkey's Audio; TrueHD and MLP; TTA and ALAC; WAV and AIFF; WMA Lossless; the measurements; the front ends.
  - Every finding was fixed in #189 to #198, one pull request per area, plus a fuzz finding in WMA superframes (#201).
- Each gate's push run and nightly run were green on every platform.

## Phase 1 (v1): MP3

- **Why MP3 first:** it forced the shared model and parser interface into shape before any second format existed. Building it first, with the verification stages, answered the two largest risks: a wrong core contract and spec details that no reviewer catches by eye.
- **MPEG audio** (MP1, MP2, MP3): frames, side information, the bit reservoir, Layer II allocation and the MPEG-2 multichannel extension, Xing, Info, LAME and VBRI headers, mp3PRO and MP3 Surround, encoder detection and the coding statistics: [mpeg-audio.md](formats/mpeg-audio.md).
- **v1 stopped** at headers, side information and metadata: scalefactor and Huffman-table level analysis was a non-goal for v1. It comes with the MPEG audio decoder in 0.3.
- **Tags:** ID3v1, ID3v2, APE and Lyrics3, and the tag detection rules: [tags](tags/README.md). The region and sync rules are in [mpeg-audio.md, "Frames and sync"](formats/mpeg-audio.md#frames-and-sync).

## Phase 2: the lossy formats

- **MP4** (MP4, M4A, M4B, 3GP, QuickTime): every box, the sample tables and fragments, iTunes and QuickTime metadata, channel layouts, gapless data: [mp4.md](formats/mp4.md).
- **AAC** in MP4, ADTS and ADIF, with every raw data block of AAC Main, LC, SSR and LTP, implicit SBR and PS, ELD and USAC configurations: [aac.md](formats/aac.md).
  - **Why the raw data blocks were read in phase 2:** HE-AAC and HE-AAC v2 may be signaled only inside the frames, in a fill element after the coded spectra. The parser reads each block far enough to reach it, without dequantizing, and the same pass gives AAC's coding statistics.
  - For the other codecs that depth comes with their decoders' entropy pass.
- **Ogg:** pages, multiplexed and chained streams, routes by the first packet: [ogg.md](formats/ogg.md).
- **Speex, Vorbis and Opus** as codec parsers on the same container, without changes to it: [speex.md](formats/speex.md), [vorbis.md](formats/vorbis.md), [opus.md](formats/opus.md).
- **Vorbis comments** as a tag parser shared by Speex, Vorbis, Opus and FLAC: [vorbis-comments.md](tags/vorbis-comments.md).
- **Musepack** SV4 to SV8: [musepack.md](formats/musepack.md).
- **AC-3 and E-AC-3,** raw and in MP4, with every audio block up to its mantissas and Dolby Atmos object audio: [ac3.md](formats/ac3.md).
  - The stream-change rule first written for this slice is shared by every raw stream format: [architecture.md, "Stream changes"](architecture.md#stream-changes).
- **ASF:** every object, packets, payloads, metadata and protection: [asf-wma.md](formats/asf-wma.md).
- **WMA versions 1 and 2, Professional and Voice,** frame by frame, as clean-room rewrites from [wma-standard-format.md](design/wma-standard-format.md), [wma-pro-format.md](design/wma-pro-format.md) and [wma-voice-format.md](design/wma-voice-format.md).
  - **Why the rewrites:** the first readers followed FFmpeg's LGPL code too closely and were removed on 2026-10-29.
  - One agent described each bitstream without code, and another, who had read neither FFmpeg nor the removed reader, wrote the reader from that description. The golden reports equal the earlier ones except for the intended differences.
  - Microsoft's decoder is the reference for WMA sample counts, run on the Windows runners; FFmpeg is the second cross-check.
- **WavPack:** lossless, hybrid and DSD, `.wv` and `.wvc`, with the block checksum recomputed: [wavpack.md](formats/wavpack.md). Its decoder came in phase 3.
- **DTS:** the core in its four forms, the core extensions and the extension substreams, raw and in MP4: [dts.md](formats/dts.md).

### Phase 2, continued

Done in this order before Gate 2:

1. **FAAC 1.31 to 2.2 detection:** [aac.md, "Encoder and writer detection"](formats/aac.md#encoder-and-writer-detection).
2. **Apple's AAC encoder fixtures** from macOS runners, and the rules that name Apple's encoder and writer: [aac.md, "What Apple's encoder writes"](formats/aac.md#what-apples-encoder-writes).
3. **Dolby Atmos detection in E-AC-3:** [ac3.md, "Object audio"](formats/ac3.md#object-audio).
4. **DTS-HD:** the lossless extension's channel sets, `.dtshd` files and the DTS:X signal: [dts.md](formats/dts.md), [dtshd.md](formats/dtshd.md).
5. **WMA Voice frames:** first built, removed on 2026-10-29 for its provenance, and back as a clean-room rewrite: [asf-wma.md](formats/asf-wma.md).
6. **Matroska and WebM:** audio tracks routed to the codec parsers, video at container level: [matroska.md](formats/matroska.md).
7. **DRM and protection reporting** in MP4, ASF and Matroska, without decrypting: [mp4.md](formats/mp4.md), [asf-wma.md](formats/asf-wma.md), [matroska.md](formats/matroska.md).
   - Decrypting stays out of scope: no key is used, no license acquired.

## Phase 3: the lossless formats and decoders

- **FLAC,** native and in Ogg, the reference implementation of the decoder contract: [flac.md](formats/flac.md).
- **The decoder contract,** the work budget and `--verify`: [decoders.md](design/decoders.md), approved 2026-10-22.
- **Monkey's Audio,** all versions parsed, 3.80 to 3.99 and 4.11 decoded, following the BSD-licensed SDK: [monkeys-audio.md](formats/monkeys-audio.md).
- **WAV, Wave64, BW64 and AIFF,** with IEC 61937 bursts routed to the codec parsers and the `pcm` decoder for the measurements: [wav.md](formats/wav.md), [aiff.md](formats/aiff.md).
- **TTA,** verified by its CRC-32s without a decoder: [tta.md](formats/tta.md).
- **ALAC** frame by frame in MP4 and Matroska, following Apple's Apache-licensed source: [alac.md](formats/alac.md).
- **WMA Lossless** frame by frame, clean-room from [wma-lossless-format.md](design/wma-lossless-format.md): [asf-wma.md](formats/asf-wma.md).
- **Decoders** for FLAC, WavPack, Monkey's Audio, and Dolby TrueHD and MLP: the codings that store a checksum over decoded audio.
  - ALAC, TTA and WMA Lossless store none, so their decoders were deferred (decision 5 of [decoders.md](design/decoders.md)).
  - The ALAC and TTA decoders were scheduled for the measurements by the project lead on 2026-11-08; they are in 0.2, and WMA Lossless's in 0.5.
- **Dolby TrueHD and MLP,** raw `.thd` and `.mlp` files and the codec parser, with the decoder that checks the lossless checks: [truehd.md](formats/truehd.md).
  - **Clean room:** FFmpeg's LGPL source is the only detailed one. One agent wrote the code-free description [truehd-format.md](design/truehd-format.md), and an agent that had not read FFmpeg implemented from it, with FFmpeg as a black-box cross-check.
  - **Scope set by what can be checked:** a field is added only where FFmpeg's source or one of Dolby's public documents states it. Dolby's documents had not been read when the description was written ([truehd-format.md, "Sources"](design/truehd-format.md#sources)).
  - **Containers:** Matroska routes `A_TRUEHD` and `A_MLP` (#229); MP4's `mlpa` is planned in 0.4 (FFmpeg writes it).

## Measurements and the GUI's graphs

- **Measurements of the decoded audio** (spectrum, spectral cutoff, spectrogram, waveform, levels, loudness and true peak), steps 1 to 5: [spectral-analysis.md](design/spectral-analysis.md). Step 6 is placed in the plan's milestones.
- **The GUI:** both slices (the main and detail windows, then the graphs linked to the tree and the summary cache), the Audio panels and the Frame Map ([frame-map.md](design/frame-map.md)): [gui.md](gui.md).
- **Coding graphs** for MPEG audio, AAC, and AC-3 and E-AC-3, with the contract for every format: [coding-graphs.md](design/coding-graphs.md). The other formats were put on hold, then placed in 0.3 and 0.4.
- **Samples and lost samples** across all formats: [samples-definition.md](design/samples-definition.md).

## Phase 3, continued

After Gate 3, in this order:

1. **Cross-checks against the formats' official decoders,** rollout steps 1 to 6 of [official-decoder-crosschecks.md](design/official-decoder-crosschecks.md): built.
   - Opus and Vorbis against Xiph's decoders, TTA against libtta-c, AAC against FDK AAC, Core Audio and Media Foundation, AC-3 and E-AC-3 against Core Audio.
   - Windows has no decoder for AC-3 and E-AC-3 on any runner image, so step 7 does not apply.
   - DTS, DTS-HD, TrueHD and MLP have no freely available official decoder; FFmpeg stays their reference.
   - Each difference from FFmpeg that would change counts, findings or goldens goes to the project lead before the analyzer follows it.
2. **lossyWAV and lossyFLAC detection:** paused by the project lead to save tokens, never meant to be postponed. On 2026-11-08 it became the first item of 0.2 after the groundwork.

## Gap audits

- **Wave 1** (#226): one name per concept across formats, and new series and tallies where a format had the concept but not the figure. The names are mapped in [development.md, "Names of statistics and series"](development.md#names-of-statistics-and-series).
- **Wave 2** (#228): the bitrate mode each stream states, container lengths, Matroska trims, stereo coding tallies, AAC buffer fullness.
- **Matroska routes** for FLAC, WavPack, TrueHD and MLP (#229), so those tracks are analyzed and decoded as in their own files.
- **Wave 3** waits for the entropy pass; it joined each lossy decoder's definition of done on 2026-11-08.

## Dropped plans

- **Phase 6, the sequenced formats** (MIDI, tracker modules and chiptunes), dropped by the project lead on 2026-11-08: a different domain from bitstream and encoder analysis.
  - The plan as it stood: these files hold instructions for making sound, not coded audio, so they were to be analyzed at the structure level and never rendered. The result model would have gained node kinds for their patterns, instruments and samples where the existing kinds did not fit.
  - MIDI: Standard MIDI Files (`.mid`, formats 0 to 2) and RMID, from the MIDI 1.0 specification: header, tracks and events, tempo and time-signature maps, programs, text and lyric events, the duration from the tempo map, and the extensions (General MIDI, GS, XG) the SysEx messages name.
  - Tracker modules: MOD, S3M, XM, IT and OpenMPT's MPTM, then MO3 and UMX, from the formats' own documentation, with libopenmpt (BSD) followed with its notice where the documentation stops, as for MO3.
  - Their scope: the header, order list, patterns, instruments and samples (bit depth, loops, compression); the song length from a walk of the order list with its jumps and loops; and which tracker wrote the file, from the version fields and the habits OpenMPT documents.
  - Chiptunes: SID (PSID and RSID), NSF, SPC, VGM and GBS, headers and metadata only; the music is code for an emulated chip.

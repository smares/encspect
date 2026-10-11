# Encspect: Project Plan

Planning date: 2026-10-10. This file is the canonical copy of the plan; keep it up to date as decisions change.

## Goals and non-goals

The tool tells you everything technically knowable about an audio file's structure, and every value it reports points back to the bytes it came from. It is a GUI and CLI analyzer for people who want EncSpot-level detail, across many more formats.

**Goals**

- Deep header, metadata and structure analysis: encoder detection, bitrate behavior, bit reservoir usage, Xing/Info/LAME/VBRI headers, and all tag formats (ID3v1, ID3v2, Lyrics3, APE and others).
- Every reported value carries its byte offset and length, so the GUI can show a structure tree next to a hex view and the CLI can emit text or JSON from the same data.
- Broad format coverage, with one self-contained parser per format (see the rollout section).
- Damaged or odd files produce findings (warnings, anomalies), never crashes.
- One codebase for Windows, macOS and Linux on x64 and ARM64.

**Non-goals for v1**

- Audio playback.
- Scalefactor and Huffman-table level MP3 analysis. v1 stops at headers, side info and metadata; the deeper pass is a later phase.
- Runtime-loadable plugins. Formats are separate projects, composed at build time.
- Editing or writing tags.
- Bundling external decoders or native libraries.

## Decisions so far

The stack is C# on .NET with Avalonia for the GUI. The code is checked by tests and review agents rather than line by line by a person, which shapes several choices below.

| Decision | Choice | Why |
| --- | --- | --- |
| Language | C# (.NET) | Reads like TypeScript; a malformed file raises a catchable exception instead of corrupting memory; one `dotnet publish` per OS and architecture |
| GUI | Avalonia UI | Native OS dialogs and tree view on Windows, macOS and Linux, x64 and ARM64 |
| CLI | Console app on the same core | Thin front end over the shared library |
| Parsers | Written ourselves, one project per format | Libraries hide the fields this tool exists to show; TagLib# is used only to cross-check |
| Playback | None | Pure analyzer; removes BASS, native binaries and licensing questions |
| Plugins | Build-time modules only | No runtime loading, so no version policy and no publishing restrictions |
| Decoding | Optional, per format | Only for waveform, spectrogram, lowpass and similar; formats without a decoder still get the full structure report |
| Hosting and CI | GitHub, hosted runners | Covers Windows, macOS (Apple Silicon) and Linux on x64 and ARM64 |
| Name | Encspect | Detenc and Framescope were already taken; Encspect was free |
| License | MIT | Default in the .NET ecosystem; each dependency's license is still checked individually |

## Architecture

Every parser depends on one small abstractions library and on nothing else; the front ends reach parsers only through a host.

```mermaid
flowchart TD
    CLI["CLI<br/>text, JSON and CSV output"] --> Host
    GUI["GUI (Avalonia)<br/>tree, hex view, graphs"] --> Host
    Decoders["Decoders<br/>optional, per format"] -.->|"waveform, spectrogram"| GUI
    Host["Host<br/>sniffs the format, runs tag and format parsers,<br/>assembles one report"] --> Tags["Tag parsers<br/>ID3v1, ID3v2, Lyrics3, APE"]
    Host --> Formats["Format parsers, one project each<br/>MP1/2/3 (v1), FLAC, Ogg, MP4, ..."]
    Tags --> Core["Core.Abstractions<br/>parser interface, byte and bit readers,<br/>result model: node tree with name, value,<br/>byte offset and length, plus findings"]
    Formats --> Core
```

The host sniffs the format, lets tag parsers claim their regions, then hands the rest to a format parser. Decoders are separate and optional, so no parser ever depends on one.

- **Result model:** every node has a name, value, byte offset, length and children; findings carry a severity and point to the node that caused them. A finding is a warning when the file breaks a specification or holds something wrong or damaged, whoever caused it and however well decoders cope, above all where readers then show or play something different; an info when what it reports is valid but worth knowing, or when the analyzer cannot check something whose cause is reported separately; an error when parsing itself fails.
- **Detection:** each parser reports a confidence from magic bytes; the file extension is only a hint.
- **Tags are separate from formats,** because ID3, APE and Lyrics3 appear in many of them.
- **Containers hand streams to codec parsers,** for example Ogg to Vorbis and Opus, MP4 to AAC and ALAC.
- **The abstractions stay internal** and may change freely until the second format works.

## v1 vertical slice: MP3

v1 is MP1/MP2/MP3 analysis at the header, side-info and metadata level, with no Huffman decoding. It is built first because it forces the shared model and parser interface into shape before any second format exists.

**File layout and tags**

- Leading junk and resync: find the first valid frame, report skipped bytes, and re-sync after damage. A header starts a stream only when two more frames with the same version, layer and sample rate follow at the positions its length predicts, or when its frames end exactly at the end of the data. Everything between frames that is not a frame is a gap: junk before the first frame, lost sync between frames, data after the last one. Each stretch of data between tags is one "MPEG Audio" region, and findings of one kind are reported once per stream with a count, not once per frame.
- ID3v2.2, 2.3 and 2.4: header, flags, extended header, footer, padding, unsynchronization, and every frame with its decoded text encoding. Two writer bugs are read the way mutagen reads them, with a finding: frame sizes that old iTunes versions wrote into ID3v2.4 as plain integers (the frames are walked both ways, and the reading that finds more declared frame IDs wins), and an extended header flag with a frame where the extended header should be. Data after the terminator of an ID3v2.3 text is information, since section 4.2 says to ignore it.
- Trailing tags: ID3v1 and v1.1, the Enhanced TAG, Lyrics3 v1 and v2, and APEv1 and v2 including every item.
- Any gaps, overlaps or trailing garbage between regions are reported as findings.

**Per-frame data (all layers)**

- Every header field: MPEG version (1, 2, 2.5), layer, CRC protection, bitrate, sampling rate, padding, channel mode and mode extension, copyright, original, emphasis.
- Frame offset, length, and CRC-16 verification when protection is on. A Layer II CRC covers the bit allocation and scfsi, so its length follows from the allocation tables.
- Mid-stream parameter changes (sampling rate, channel mode) and free-format streams. A stream change is reported both where frames of two streams touch and where sync is found again after a gap. A free-format frame's length is the distance to the next header of the same stream, as mpg123 finds it; one too short for the header, CRC and side information or bit allocation is no frame, so data with a header every few bytes does not sync. A free-format stream whose frame length changes breaks the fixed bitrate ISO/IEC 11172-3 requires and is a warning.
- Once in sync, a frame is only taken on its header while a frame follows it. Where none does, a stream that starts inside the frame cuts it short there, reported as a frame that was cut or whose header is damaged, so the next frame is not swallowed and the damage is located; a free-format stream may instead have changed its frame length.
- MPEG 2.5 extends only Layer III, so Layer I or II frames at its rates are a warning; decoders play them.
- Leading junk is searched for the first frame within 1 MiB after any leading zero bytes, which are common padding and cheap to skip.

**Layer I audio data**

- Bit allocation per subband and channel, one above the joint stereo bound, scalefactors, the length of the samples and the ancillary data after them (ISO/IEC 11172-3 2.4.1.5). The forbidden allocation 15 is a warning (libmad rejects the frame, mpg123 reads 16-bit samples, as the report does), and so are scalefactor index 63 and audio data longer than the frame.

**Layer III side info**

- `main_data_begin` and the resulting bit reservoir fill for each frame: the free bytes after the previous frame's main data, up to what `main_data_begin` can reach (511 bytes in MPEG-1, 255 in MPEG-2 and 2.5).
- Per granule and channel: `part2_3_length`, `big_values`, `global_gain`, `scalefac_compress`, block type and mixed-block flag, table selects, region counts, `subblock_gain`, `preflag`, `scalefac_scale`, `count1table_select`, plus `scfsi` per channel.
- Joint stereo: MS and intensity usage per frame.
- The bit reservoir: each frame's bytes after the side information are split by owner into its own main data, main data of later frames ("Bit Reservoir") and ancillary data no frame uses, so every byte is accounted for. Main data runs through the data of consecutive frames of one stream, skipping headers and side information, and continues across junk and tags between frames; a stream change starts with an empty reservoir. Main data that begins before the stream (a cut file), overlaps the previous frame's (missing frames), reaches back across a gap (decoders disagree there) or does not fit the frame is reported. A Xing, Info or VBRI header frame stays in the reservoir as a frame without main data, as minimp3 and mp3guessenc keep it; mpg123 and ffmpeg skip it, so main data that begins in it is a warning, as after `ffmpeg -ss -c copy`, and the header frame's unused bytes show which frame's main data they hold. The header frame does not count as the stream's first frame, so main data that reaches back past it begins before the stream.
- Side-information checks: big_values above 288, the reserved block type, Huffman tables 4 and 14, scalefactors longer than part2_3_length, scfsi on a channel with short blocks, and MS or intensity stereo with different block types in the two channels (the last two are rejected by libmad).

**Layer II audio data and multichannel**

- Bit allocation per subband and channel from the allocation tables (ISO/IEC 11172-3 Annex B, and ISO/IEC 13818-3 for the lower sampling rates; free format takes Table B.2a at 48 kHz and B.2b at the other rates, as their captions say and libmad reads it, while mpg123 takes B.2a at every rate), scfsi and scalefactors, the length of the sample data and where the ancillary data starts. The same tables let Layer II CRCs be verified. Audio data longer than the frame is a warning; scalefactor index 63, which Table B.1 of ISO/IEC 11172-3 leaves undefined, is a warning too, although TwoLAME writes it and ffmpeg, mpg123 and libmad decode it.
- The MPEG-2 multichannel extension (ISO/IEC 13818-3), which an MPEG-1 Layer II frame carries in its ancillary data, so the stereo base stays playable: the multichannel header (center, surround and LFE channels, audio mix, dematrix procedure, multilingual channels with their layer and sampling rate, whether an extension bitstream exists), the channel configuration such as 3/2 plus LFE, its CRC, and how many bytes the multichannel data takes. The frame header has no flag for it, so it is detected by structure and consistency across frames, like a tag, never by a single match: a file carries it once the multichannel CRC holds in at least two frames and in at least half of the frames whose multichannel side information fits the frame. Random ancillary data passes the 16-bit CRC about once in 65,536 frames, so a long recording passes it a few times by chance; mp3guessenc likewise wants a share of the stream. Then each frame whose CRC fails is reported, and so is the reserved center value, which is read as no center.
- Layers I and III can carry the extension too; the ISO conformance set has one stream of each (tests 22 and 23). They follow later.
- When an extension bitstream exists, the multichannel data continues in it, after the base frame's bits minus `n_ad_bytes` of MPEG-1 ancillary data at its end. The extension bitstream is a separate file, so frames whose multichannel side information does not fit the base frame are reported as continuing there, with their CRC not verified. When no frame's side information fits, no CRC can confirm the extension; if nine in ten candidate frames carry the same multichannel header and continue in an extension bitstream, that is reported as a possible, unverified extension.

**Encoder and VBR headers**

- Xing and Info: flags, frame and byte counts, the seek table, quality. They are read only in Layer III, where the side information would end without a CRC (36, 21 or 13 bytes into the frame, as LAME writes them even into a protected frame), and only when every byte between the frame header, or its CRC, and the header is zero, as mpg123 requires, so audio data that happens to contain "Xing" is not taken for one. A header that does not fit its frame and a TOC that decreases are warnings. The frame decodes as silence in decoders that do not skip it, so it stays in the bit reservoir as a frame without main data (see the bit reservoir above).
- LAME extension, read only after an encoder string that starts `LAME`, `L` and a version (`L3.99a12`) or ffmpeg's `Lavc` or `Lavf`, as mp3guessenc and ffmpeg require; other text there is shown as a string and names nothing: encoder string, tag revision, VBR method, lowpass, peak, ReplayGain fields, encoding flags, ATH type, minimum or ABR bitrate, delay and padding, noise shaping, stereo mode, source sample rate, MP3 gain (a sign bit and seven bits of 1.5 dB steps), preset, music length (0 means not set, an info), tag CRC and music CRC (both verified). The tag CRC covers the frame's bytes before it, the music CRC the bytes after the frame up to the music length, which counts from the start of the frame. A failing tag CRC or music CRC is a warning, also where the encoder is to blame: the field breaks the tag's specification and readers act on it. Two such causes get their own finding, which names them: ffmpeg writes the tag CRC over the frame's first 190 bytes wherever the tag starts, which is only correct for MPEG-1 stereo, so LAME and ffmpeg itself reject it and ignore the tag's encoder string and ReplayGain; and in a file that continues a `--nogap` sequence LAME continues the previous file's music CRC, so it cannot confirm that the audio is unchanged, which an edit such as MP3Gain's would look no different from. Otherwise a failing music CRC means the audio changed after encoding. A music length shorter than the tag's own frame leaves the music CRC unverified, with its own finding; so does one that runs past the end of the file, an info where the stream is cut short, which its own findings report, and a warning where the stream holds every frame its header counts. LAME before 3.90 wrote only a version string, which is reported as such. LAME 3.90 to 3.92 have Dibrom's `--alt-preset` modes and the older `--preset` names but leave the preset field empty, which 3.93 added; where the VBR method, Xing quality, noise shaping, stereo mode, ATH type, lowpass and (but for VBR) bitrate field are those a preset sets, the report names it next to the empty field. The settings come from encodes with every preset by 3.90 and 3.92 built from source and by the Windows builds of 3.91 and of HydrogenAudio's 3.90.3, the 3.90 line it recommended for years, keyed as getID3 keys them: those two write some lowpasses 100 Hz higher (getID3 lists the same for 3.91), and 3.90.3 shapes noise differently in standard and extreme. getID3's other 3.90.3 values (and its medium presets) belong to a modified build of it and are not used. 3.90.3's version, "LAME3.90.3", does not fit the tag's nine bytes, which hold "LAME3.90."; LAME writes it whole into the padding of the frames, where the analyzer completes it from, as getID3 and mp3guessenc do. LAME before 3.95 computed ReplayGain against 83 dB instead of 89 dB, so a gain it determined itself reads 6 dB lower than a later version's; the report says so next to the value, which it keeps as stored (mpg123 adds the 6 dB).
- VBRI (Fraunhofer): version, delay, quality, byte and frame counts, seek table, 36 bytes into the frame in every mode. A version other than 1, which ffmpeg ignores, and a seek table entry size outside 1 to 4 bytes, which leaves the table unread, are warnings.
- A VBR header in the middle of a stream, after any earlier frame of it, across gaps and tags, is a warning: files were probably joined, so the first header's counts and TOC cover only part of the stream, and players that only look at the first frame play the second as a frame of silence.
- Encoder detection from these tags, with a clearly labeled confidence level when it has to fall back on heuristics for files without tags: a LAME tag names its encoder (high confidence when its CRC holds, medium when not), a VBRI header Fraunhofer (high), or Fraunhofer's MP3 Surround or mp3PRO encoder when the ancillary data carries their signature. ffmpeg writes a LAME tag into every MP3 it muxes, with the encoder string of the stream it copies (a remux of a LAME file keeps `LAME3.100`) or its own: `Lavc` where libavcodec encoded, which it does only with libmp3lame or libshine, so LAME's version in the ancillary data or Shine's traits behind it are medium confidence, and other evidence keeps its own; without any, `Lavc` names ffmpeg (medium). `Lavf` marks a remux of a stream whose encoder ffmpeg did not know and names no encoder: the stream's evidence names it as it would alone, with the tag as support.
- A header names the encoder of the frames it describes, from its frame to the next header or a change of stream. Only a header in a region's first frame names the region's encoder from its tag; where the region also holds frames it does not describe, or headers that name another encoder, files were joined and that name is at most medium confidence. A header further on names no more than the frames after it: the stream's other evidence names the region's encoder, which such a header supports where it names the same, and without any the header names it with low confidence. Each header's counts and gapless figures cover its own frames and say so where those are not all of the region's.
- LAME writes its version twice (libmp3lame/version.c): into the tag's nine bytes, `LAME`, major.minor and a marker (`a` alpha, `b` beta, `r` a release with a patch level above 0 that does not fit, a space otherwise; 3.99 alphas wrote `L3.99a12`, 3.100 and later have no room for a marker), and whole into the padding it drains into ancillary data (`3.98 (beta)`, `3.99 (alpha 12)`, `3.99.5`). The report spells the version out (`LAME 3.98 (beta)`, `LAME 3.99 (alpha 12)`, `LAME 3.99.x` for an `r` without a patch level) and keeps the raw text in the tag's node. Where the two agree on major.minor and marker, the name takes the more specific one and lists both as evidence when the ancillary one carries a marker; where they disagree, the tag's is shown and a warning says the frames and the tag may come from different encoders.
- The maintained Helix encoder (hmp3 5.2.4) writes a LAME tag whose encoder string is `LAMEH5.24`; it names Helix, not LAME, with the tag's confidence.
- Without a tag or signature, an encoder's habits are low-confidence evidence, used only where encodes of known signals with that encoder show them every time and the other encoders available for testing do not, over at least 20 frames: Shine never uses the bit reservoir, short blocks, scalefactor scaling or ancillary data (behind ffmpeg's `Lavc` tag, which only libmp3lame and libshine encodes carry, that is medium confidence); Helix without its tag (5.2.4 and 5.0 r11) sets the copyright and original bits in every frame and uses scfsi and scalefactor scaling; Xing's encoder (tompg 3.0) never switches to short blocks, not even at a tone's onset where every other encoder does, never uses scfsi, and sets the original bit in every frame; BladeEnc switches to short blocks without scalefactor scaling, codes plain stereo or mono at one bitrate in MPEG-1, and from 0.82 on leaves ancillary data at most in its last frame (checked on every surviving source release from 0.80 to 0.94.2 and the Windows builds of 0.82, 0.90 and 0.91; 0.80 and 0.81 drain an overflowing bit reservoir into ancillary data mid-stream, as the ISO dist10 encoder does, and mp3guessenc does not name them either); Fraunhofer's encoders use scalefactor scaling and short blocks but never scfsi (MPEG-1 only, since MPEG-2 has no scfsi); TwoLAME and Xing both write scalefactor index 63 into Layer II, which names them together. mp3guessenc agrees on every Layer III encode of these encoders; its rules for encoders that cannot be run here (older releases) are not used, since on encoders that can, it names Shine "Xing" and Helix "Lame". Silence gives an encoder nothing to decide, so its traits do not show there, and a silent stream may match another encoder's habits.
- A strong hint without tags: encoders fill unused bits with their name and version. LAME writes `LAME3.100` (possibly cut short to `LAME`) into ancillary data mid-stream and into the padding of the last frame, even when the Info tag is disabled; TwoLAME writes zeros. A version that runs to the end of the ancillary data may be cut short there (`LAME3.10`), so a whole one elsewhere is preferred and a cut one names only LAME. The string starts wherever the main data ends, at any bit position, and can run on into the next frame's data, so it is searched at every bit offset in the ancillary data the bit reservoir split identifies, joined across frames, not in raw frame bytes. Found alone, it is low confidence. Which other encoders sign their padding, and how, is established from encoder-written fixtures before it is used: Gogo-no-coda (3.12) fills the bits no frame uses with `GOGO` over and over, found at any bit position as two in a row (low confidence alone), and in VBR writes `GOGO` where its Xing header's quality belongs, which names it with medium confidence and is shown as such instead of a number.
- An ID3v2 TSSE frame (TSS in ID3v2.2) is the weakest evidence: whatever software writes the tag fills it in, which may be the encoder (LAME's front end writes `LAME 64bits version 3.100 (http://lame.sf.net)`), a later tagger, or ffmpeg's muxer, and it is kept when the audio is re-encoded. The tag parser hands the text to the host, which passes the claims of every accepted tag, in file order, to the format parser. Alone, a claim names the encoder with low confidence; naming the same encoder as low-confidence evidence from the stream, it raises that to medium (names match on whole words, so `Flame` does not name LAME nor `Remixing` Xing), and it never lowers or overrides a LAME tag or VBRI header. ffmpeg writes its own version (`Lavf60.16.100`) whatever encoder it used, so its claim supports the stream's encoder without naming one, and alone names none. A claim that names another encoder than the stream points to is an info finding on the tag frame, not a change of encoder.
- mp3PRO and MP3 Surround keep their extension in the ancillary data of every Layer III frame: mp3PRO its spectral band replication data, from which an mp3PRO decoder restores the high frequencies of an MPEG-2 or 2.5 core, and MP3 Surround the spatial data that turns an MPEG-1 stereo downmix at 44.1 or 48 kHz back into 5.1 channels. No specification is public, so detection follows mp3guessenc: each frame's ancillary segment runs from the end of its main data, rounded up to a byte, to where the next frame's main data starts in the reservoir, and the bits that the first two bytes of every segment share must include 0xC0 and 0x08 (mp3PRO) or 0xCF and 0x30 (MP3 Surround). An empty segment rules both out, and unlike mp3guessenc at least eight segments are needed, so a short stream does not match by chance. The region gets an Enhancement node with the ancillary bytes and their data rate, and an info finding says that decoders without support play only the core or the downmix. Both name the encoder with medium confidence: Fraunhofer's MP3 Surround encoder, or an mp3PRO encoder, since several licensees wrote those.
- Fraunhofer's encoders may write an Original File Length (OFL) block at the start of the first frame's ancillary data: 0xB4, a 16-bit delay and a 32-bit original length in samples, recognized by mp3guessenc's rule (MPEG-1, the delay's high byte 0x04 or 0x08; in MP3 Surround the signature follows at bytes 10 and 11). The delay counts the samples before the audio in a plain decoder's output, the decoder's 529 included, which ffmpeg's output of encoder-written fixtures confirms; mp3guessenc adds 576 for MP3 Surround, the report keeps the stored value. The block names Fraunhofer with medium confidence, and a delay and length longer than the stream are a warning. A VBRI header still decides the encoder, with high confidence. In mp3PRO (MPEG-2 and 2.5), mp3guessenc's rule reads a different block: more than 20 bytes starting 0xE0 0xBA, the delay at byte 14 and the length at byte 16, counted at twice the core's sample rate, the rate an mp3PRO decoder outputs; a real encode by Thomson's mp3PRO encoder stores 88200 samples for two seconds at 44100 Hz in a 22050 Hz stream.

**Computed statistics**

- A Statistics node in each MPEG Audio region, over its complete audio frames (not a frame holding a VBR header, not a truncated last frame): frames, samples and duration, whether the bitrate varies, the average, minimum and maximum bitrate, and the bitrate distribution. Time runs on a clock of 1/14,112,000 s (ffmpeg's time base for MP3, which every MPEG sample rate divides), so frames of different sample rates add up exactly, and figures are rounded half to even.
- Bitrate over time and bit reservoir fill over time, one value per second of audio as a comma-separated list: the bitrate of the frames that start in that second, in kbps, and their mean reservoir fill, in bytes (Layer III only). The GUI's graphs can use the per-frame values instead.
- Counted frames and bytes compared with what the Xing or VBRI header claims, over the frames from the header to the end of its stream (the next header or a change of version, layer or sample rate, across gaps and tags). A truncated last frame was written whole, so it counts there and in the gapless sample count. Xing's and Fraunhofer's encoders count the header's own frame and LAME does not, so either count holds; a mismatch is a warning. Behind a LAME tag the count also sets where the music ends, since the delay and padding count from the frames after the header: LAME 3.94 beta 1 counts the header frame too, so ffmpeg and mpg123, which take the length from the count, play one frame more than was encoded. That is a warning, and the report's own sample count follows the frames.
- Gapless information from a LAME tag: encoder delay, padding, and the exact sample count, the frames' samples minus both, which is what ffmpeg and mpg123 output with their gapless trimming when the padding covers their own 529-sample decoder delay. Delay and padding longer than the stream are a warning.
- Cross-checked with mp3guessenc (frame count, length, average bitrate, bitrate histogram), where it takes the same frames as audio, and with the samples ffmpeg and mpg123 decode; at an exact tie their floating-point rounding may differ from ours by one in the last digit.

## Tag detection and false positives

Tags are found by structure and byte ownership, never by signature alone, because short signatures collide with each other and with audio data.

**Rules**

1. **Position-anchored search.** Each tag type is looked for only where its spec allows: ID3v1 at exactly EOF-128, APE through its footer at EOF-32 or just before another trailing tag, or through its header at the start or directly after a leading tag (APEv2 only, since APEv1 has no header), ID3v2 at the start or directly after a previous tag. The one guided search: an ID3v2.4 SEEK frame gives the minimum offset of the next tag, and ID3v2.4 section 5 says to search from there, so the host searches at most 1 MiB of unclaimed data from that offset and accepts only a fully validated tag. Signatures are never scanned for across the whole file.
2. **Structural validation decides confidence, not existence.** A candidate is rejected only when its extent cannot be determined: an ID3v2 size that is not syncsafe, an APE size that points outside the file, a Lyrics3 v2 size that does not point at `LYRICSBEGIN`, a footer without a matching header. Everything else that breaks a specification is reported the way a lost MPEG sync is: the tag stays in the report, each violation becomes a finding, and the confidence drops; the host enforces it, so a tag with any warning is at most medium. An ID3v2 size that runs past the end of the file does not say where the tag ends either, so such a tag ends where its frames stop, and what follows is left to the other parsers. Full confidence needs everything to check out: for ID3v2, known flags, an extended header laid out as its version defines it, at least one frame, frames that fit the declared size and padding only at the end, frames of a kind the specification allows once only once (text and URL frames other than the user-defined ones, WCOM and WOAR, a picture of type 1 or 2, and frames such as MCDI or PCNT), a data length indicator with every compressed ID3v2.4 frame, and a byte order mark on every UTF-16 string; for APE, header and footer that agree in everything but the is-header flag, a footer that does not claim to be missing, items that end exactly at the footer and text items in UTF-8; for Lyrics3 v2, at least one field, fields that end exactly at the size and field bytes from 01 to 254; for Lyrics3 v1, text without $00 or $FF within 5100 bytes. Lyrics may mention `LYRICSBEGIN`, so a Lyrics3 v1 tag starts at the earliest one in those 5100 bytes after which only text follows, and a later one is noted. ID3v1 has almost nothing to validate, so it never rises above medium confidence.
3. **Evaluation order and ownership.** Tags are only looked for at anchors (rule 1). When several candidates claim the same anchor, the one with the strongest evidence wins and the order APE, Lyrics3, ID3v2 found through its footer, ID3v1, Enhanced TAG breaks ties. A tag that its specification places directly before another one (Lyrics3 and the Enhanced TAG before ID3v1) is reported without it too, with a finding and one level less confidence, so a correctly placed competitor wins. The host keeps a claim map, so a signature inside an already claimed region (an image in an ID3v2 tag, an APE item, an ID3v1 field) is rejected.
4. **Confidence and evidence.** Every candidate carries a confidence level and its reasons. Losing and rejected candidates become findings, such as "TAG at EOF-128 rejected: part of an APE tag header".
5. **MPEG sync.** A frame header counts only when a chain of consecutive, consistent frames follows it. Stretches that do not fit are reported as gaps, not guessed.

**The APE and ID3v1 collision.** `APETAGEX` contains `TAG` at offset 3. An APE tag with a header that totals 131 bytes (32 header, 67 items, 32 footer) and ends the file places that `TAG` at EOF-128, exactly where ID3v1 is expected. A reader that trusts the signature reports a garbage ID3v1 tag made of the APE header, items and footer. Rule 3 prevents this: the APE tag validates fully, its evidence outweighs the bare `TAG`, and it claims the bytes.

**Fixtures that prove it**

- The 131-byte APE tag above with no ID3v1 tag: expect the APE tag only, plus a near-miss finding.
- A real ID3v1 tag directly after an APE tag, and an APE tag after an ID3v1 tag.
- Lyrics3 v1 and v2 before ID3v1, and every plausible stacking order of ID3v2, APE, Lyrics3 and ID3v1.
- ID3v2.4 tags found through their footer: at EOF, before APE and ID3v1, at both ends of the file, as the whole file, and with `TAG` at EOF-128 inside the tag. A tag appended without a footer cannot be found from the end (ID3v2.4 section 3.4) and stays data.
- Tags embedded inside cover art in an ID3v2 tag.
- Random data seeded with `TAG`, `ID3` and `APETAGEX` at random offsets: nothing is detected outside validated positions.
- User text that spells another tag's identifier exactly where a probe looks for it: ID3v1 fields, APE text items, Lyrics3 lyrics and fields, and appended ID3v2 frames, each holding every identifier at every probe distance. The real tags must come out unchanged, without warnings.
- Truncated tags and concatenated files.

The first fixture needs no external tool; a script writes the exact bytes.

## Verification strategy

Nobody will read the code line by line, so correctness comes from automated checks that do not depend on the author's judgment. The weak spot to guard against is spec details written from memory, such as field offsets and odd edge cases.

1. **Known-input fixtures.** Scripts in `scripts/fixtures` write two kinds of fixture, each with a manifest of the expected result. Hand-built ones (standard library only, regenerated and compared in CI) control every byte. Encoder-written ones (LAME, TwoLAME, mutagen, ffmpeg, pinned versions) are committed, because their bytes depend on the tool version. Tests assert that the analyzer reports exactly what was built or encoded.
2. **Cross-checks against other tools.** Where results overlap, compare with `ffprobe` and TagLib#. Disagreements fail the build until explained. For MPEG audio, a CI job checks every fixture manifest against ffprobe (frame positions and lengths), libmad through madplay (CRC verdicts, and frame counts where ffmpeg resyncs differently) and mpg123 (frame counts of free-format streams, which ffmpeg cannot read), so the expectations are confirmed by independent decoders before the parser is. For Layer III, minimp3 (a pinned commit, built in CI) confirms every side-information field and the bit reservoir before each frame, mp3guessenc confirms block usage, stereo modes, global gain, the largest main_data_begin and the ancillary data, and the hand-built frames carry main data that ffmpeg, mpg123 and libmad must decode to pure silence, which they only do when every granule's bits sit where the side information and the reservoir say. For Layer II, minimp3 confirms every allocation, scfsi and scalefactor, the hand-built frames decode to silence in the same decoders, and mctoolamed and mp3guessenc confirm the multichannel extension; the analyzer's report on all 35 ISO/IEC 13818-4 audio conformance streams must match their manifest. For the Xing, Info, LAME and VBRI headers, mp3guessenc confirms nearly every field and both CRC verdicts, mpg123 the counts, encoder string, VBR method, ReplayGain, delay and padding, and ffprobe the duration, start time, and the encoder string and ReplayGain it only accepts when the tag CRC holds; the peak is the one field no tool reports. getID3 (a pinned commit, run with PHP in CI) confirms the version and preset of every fixture by LAME 3.90 to 3.92. mp3guessenc also confirms the encoder string in ancillary data, where it finds it on a byte boundary, the mp3PRO and MP3 Surround signatures and the OFL block in hand-built and Fraunhofer-encoded fixtures, and the ancillary bytes and rate of three mp3PRO files from the FFmpeg sample archive, which CI downloads at pinned hashes because they are commercial music. Encoder-written fixtures come from LAME, TwoLAME, Shine, ffmpeg, Fraunhofer's MP3 Surround encoder, the Helix encoder (5.2.4 and 5.0 r11 from source), BladeEnc 0.80, 0.82, 0.84, 0.92.0, 0.92.7, 0.93.6 and 0.94.2 (built from source, one release per group that writes the same bytes; those before 0.92 as 32-bit programs, as they assume a 32-bit long), LAME 3.90.1, 3.91, 3.92, 3.93.1, 3.94 beta 1, 3.98 beta 8, 3.99 alpha 12, 3.99.5, 3.101 and 4.0 (built from source, the betas, the alpha and 3.99.5 from tags of LAME's Subversion repository; 3.101 writes "LAME3.101" with no room for the release marker, 4.0 writes "LAME4.0 " and a NUL byte), the Windows builds of LAME 3.91 and 3.90.3 (under Wine; the project's private repository keeps 3.90.3, which only a download site still serves), Gogo-no-coda 3.12 (its Windows release under Wine) and Xing's tompg 3.0 under Wine, the only source of encoder-written Layer I, whose forbidden bit allocations libmad rejects as the analyzer reports them. Encoders whose licences allow private evaluation only (Fraunhofer's l3enc 2.60 and 2.61, the mp3enc 3.0 and 3.1 demos, Thomson's mp3PRO encoder) are run by hand with check_restricted_encoders.py, which keeps nothing they write: the Fraunhofer trait rule, mp3guessenc and the mp3PRO OFL block are checked on their output.
3. **Golden reports.** Each fixture has a stored expected report; any output change shows up as a diff that has to be approved.
4. **Corrupt-file tests and fuzzing.** Truncated, bit-flipped and garbage-prefixed files must never crash or hang the tool; they must yield findings. The tag-collision cases are listed under tag detection.
5. **Spec references in the code.** Each parsed field cites the spec section it implements, so a reviewer can check it quickly.
6. **Independent review.** After each milestone, a separate review agent gets the code and the spec without the author's conclusions; its findings are recorded in the pull request.

Nothing merges with a red pipeline, and nothing is pushed to GitHub unless the owner says so for that push.

## GitHub and CI

GitHub Actions is the single source of truth for "does it build and pass on every platform", so the work can be verified without anyone's computer being on. The repository is `smares/encspect` (public, which keeps hosted-runner minutes free).

**Matrix.** Windows, macOS and Linux, each on x64 and ARM64 where a hosted runner exists. Exact runner labels are checked when the workflow is written, since GitHub changes them from time to time.

**Pipeline stages**

1. Restore and build with warnings treated as errors.
2. Formatting check (`dotnet format --verify-no-changes`).
3. Unit tests for every parser.
4. Install `lame`, `ffmpeg` and `flac`, generate fixtures, run integration tests and the cross-checks.
5. Corrupt-file and fuzz smoke tests.
6. Headless GUI tests using Avalonia's headless mode.
7. Publish self-contained CLI and GUI builds for each target (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, and `osx-x64` if a runner or cross-build is available) and attach them to the run.
8. On a version tag, create a release with those builds.

**Local checks.** Cloud sessions install the .NET SDK through a SessionStart hook (`scripts/install-dotnet.sh`), so the CI steps can be mirrored locally before pushing. CI still covers the platforms a session cannot run.

## Format rollout

MP3 comes first, and each later phase starts only after the previous one passes its gate.

| Phase | Scope |
| --- | --- |
| 1 (v1) | MP1/2/3 and tags: all tags, side info, Xing, LAME, VBRI, reference tests |
| Gate 1 | Pipeline green everywhere, review findings fixed, abstractions settled |
| 2 | FLAC, WAV, AIFF, Ogg with Vorbis and Opus (Ogg hands streams to codec parsers) |
| Gate 2 | Pipeline green everywhere, review findings fixed |
| 3 | MP4 with AAC and ALAC, WavPack, Monkey's Audio, TTA |
| Gate 3 | Pipeline green everywhere, review findings fixed |
| 4 | Long tail: Musepack, WMA, AC-3; TAK and OptimFROG at header level only |
| Later, order not fixed | MP3 scalefactor and Huffman analysis; decoders for waveform and spectrogram, format by format |

A gate means the pipeline is green on every platform and the review agent's findings are resolved; the first gate also requires the shared contract to have settled. TAK and OptimFROG are poorly documented, so they stay at header level. The deeper MP3 pass and the decoders are not tied to a phase yet.

## CLI and GUI

Both front ends are thin views over the same result model, so neither can show something the other cannot.

**CLI**

- Analyze one file, several files or a whole folder tree (recursive, with a format filter).
- Output as readable text, JSON, or CSV for batch summaries; exit codes reflect whether findings of a given severity were found.
- Options to include or hide sections (tags, frames, side info, statistics) and to print byte offsets.

**GUI**

- Folder tree and file list using native OS dialogs; drag and drop; batch analysis in the background.
- Report view with the structure tree, where selecting a node highlights its bytes in a hex view.
- Findings list with severity, filterable and linked to the node that caused them.
- Graphs: bitrate over time, bitrate distribution, bit reservoir fill, all from header-level data and available in v1.
- Waveform and spectrogram panels that need decoded audio. These appear only for formats that have a decoder and otherwise say so.
- Export of the report as text or JSON.

## Risks and open questions

The largest risks are a wrong core contract and spec details that no reviewer will catch by eye; both are addressed by building MP3 first and by the verification stages above.

**Risks**

- **Core model reworked late.** If the result model or reader APIs are wrong, every format pays. MP3 goes first and the contract stays internal and changeable until the second format works.
- **Spec details wrong.** Field offsets and edge cases written from memory can be off; known-input fixtures and tool cross-checks are the safety net.
- **Decoders.** Quality, license and upkeep of managed decoders vary, and some formats (AAC, Musepack, Monkey's Audio) may have none. Waveform and spectrum are therefore optional per format.
- **Closed formats.** TAK and OptimFROG are poorly documented, so they get only what their headers reveal.
- **Heuristic encoder detection.** Files without LAME or Xing tags can be misidentified; results carry a confidence label. Encoder habits are only trusted where encodes with that encoder confirm them, which leaves out encoders that no longer run.
- **Tools and references disappearing.** The checks rely on old tools and on files the repository may not hold. minimp3 and mp3guessenc are committed as source in `third_party` and built in CI. The ISO streams, the mp3PRO samples, mctoolamed and Fraunhofer's encoder are downloaded at pinned hashes, with the Wayback Machine's copy as a fallback where it serves one (the ISO streams and the mp3PRO samples; mctoolamed only SourceForge serves, and Fraunhofer's encoder only Rarewares); the ISO streams and the mp3PRO samples have a last copy in the project's private repository, which CI reads with a read-only token, and a weekly workflow downloads each from every address, so a vanished source is noticed while a copy remains. The encoders only regenerate committed fixtures, so losing one stops new fixtures, not the checks.
- **References for mp3PRO and MP3 Surround.** Neither has a public specification, and no mp3PRO encoder runs today; detection follows mp3guessenc and is checked against three downloaded mp3PRO files and against fixtures from Fraunhofer's MP3 Surround encoder 1.5 (`fhg_tool.py`), whose limited-use licence the project's owner accepted for this non-commercial project. What the extension data holds is not read.
- **References for MPEG-2 multichannel.** No tool in Ubuntu encodes or fully decodes it. The ISO/IEC 13818-4 conformance streams (22 multichannel Layer II streams covering every configuration, LFE, phantom center, prediction, dynamic crosstalk, multilingual channels and extension bitstreams) may not be redistributed, so CI downloads them at pinned hashes; mctoolamed, a decoder derived from the ISO reference software that reproduces their reference output, and mp3guessenc, which verifies the multichannel CRC, check them and the hand-built fixtures.
- **Large inputs.** Multi-gigabyte files and 100,000-frame reports need streaming reads and a lazily loaded, virtualized tree in the GUI. Parsers keep repeated parts such as audio frames as compact records and give their nodes a build function, so a node's children exist only while something reads them; reports are written as they are read.

**Open questions**

- Whether an optional, not bundled `ffmpeg` fallback for decoding is acceptable.

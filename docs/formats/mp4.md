# MP4

MP4, M4A, M4B, 3GP and QuickTime files.

## What is read

Box by box:

- every box's header;
- the movie, track and media headers;
- sample descriptions, with their esds box and QuickTime's sound descriptions;
- the sample tables, edit lists, roll groups and movie fragments;
- iTunes metadata with every data type, iTunSMPB, Encoding Params, an ID32 box's ID3v2 tag and Nero's chapters;
- QuickTime's chan atom (Core Audio's layout tags, channel bitmap and channel labels) and the ISO chnl box;
- protection: the sinf box of a protected sample entry (frma, schm, schi with tenc), pssh, senc, saiz and saio boxes, the `seig` sample group, and the entries and boxes of FairPlay and Audible (see "Protected tracks").

Each audio track's samples go to its codec's parser: [AAC](aac.md), [MPEG audio](mpeg-audio.md) for MP3 in MP4, or [AC-3 and E-AC-3](ac3.md) or [DTS](dts.md).

## Findings

- broken box sizes and inconsistent sample tables;
- samples outside the file or outside mdat;
- durations that disagree;
- moov after mdat;
- an edit list and iTunSMPB that disagree on the encoder delay or padding, or an iTunSMPB whose total is not the track's duration (Apple Lossless's duration leaves out the padding, which is accepted);
- a chan or chnl layout that disagrees with the codec's.

## Protected tracks

A protected track is reported on its stream (`StreamInfo.Protection`: the scheme, whether the samples are encrypted and the DRM systems the file names), in the JSON as `isProtected` and `protection`, in the CSV and in the GUI. Nothing is decrypted, and no key or license is read or fetched.

- **Sample entries:** `enca` and `encv` (and the other `enc*` entries) are read as the original format that their `sinf` box names in `frma`, so an `enca` entry of AAC, AC-3, E-AC-3, DTS or MPEG audio is routed to that codec. `drms` and `drmi` (FairPlay, the entries of iTunes Store purchases) and `aavd` (Audible) are protected whatever they contain; `aavd` has the layout of `mp4a`, as GStreamer's `qtdemux` reads it.
- **sinf:** the original format (`frma`), the scheme (`schm`: type, version and URI) and the scheme's data (`schi`). A protected entry without `sinf`, `frma` or `schm` is a finding (`mp4.protected-entry-no-sinf`, `-no-frma`, `-no-schm`); without `frma` the coding is unknown and the samples are not analyzed.
- **Common Encryption** (ISO/IEC 23001-7): the schemes `cenc`, `cens`, `cbc1` and `cbcs`, named from the MP4 registration authority's list; `tenc` (default IsProtected, per-sample IV size, KID, the crypt and skip blocks of version 1, a constant IV) with findings for an IV size that is not 0, 8 or 16 (`mp4.tenc-iv-size`) and a protected track with no usable constant IV (`mp4.tenc-constant-iv`), as GPAC and FFmpeg check them; `pssh` (versions 0 and 1, the system ID named from DASH-IF's registry, the key IDs and the data size); `senc` (flags and the number of samples), `saiz`, `saio` and `seig` sample groups. A `cenc`-family scheme without `tenc` is a finding (`mp4.protected-entry-no-tenc`).
- **Encrypted samples:** a track is encrypted when its scheme is not Common Encryption, when `tenc` sets the default to protected, or when a `seig` group protects samples. The samples of such a track are never handed to a codec parser: AAC, AC-3, E-AC-3 and DTS read the configuration of the sample entry (`esds`, `dac3`, `dec3`, `ddts`) without packets, and the stream shows "frames not read: encrypted"; for MPEG audio, which has no configuration, the container describes the stream from the sample entry. The duration, samples and average bitrate come from the sample tables. An info finding says so (`mp4.samples-encrypted`).
- **Clear protected tracks:** where `tenc` sets the default to not protected and no sample group protects samples, as in a clear lead-in, the samples are read like any other, the stream is protected but not encrypted, and an info finding says so (`mp4.protected-samples-clear`).
- **FairPlay:** the `sinf` box of a `drms` entry holds `frma` and the atoms `user`, `key `, `iviv` and `priv` in `schi`. A source lists them (VLC's `libmp4.c` skips them as the DRM atoms of a `drms` entry; MediaInfo reports the encryption as iTunes), but none describes their contents, so they are shown by size.
- **Audible:** the `adrm` box of an `aavd` entry is split as FFmpeg's `mov_read_adrm` reads it: 8 bytes, a 56-byte blob, 4 bytes and a 20-byte checksum. A shorter box is a finding (`mp4.adrm-short`).
- **iTunes purchases:** the account (`apID`), purchase date (`purd`), owner (`ownr`) and store IDs are metadata items of the iTunes tag, never protection; a file with them and an ordinary `mp4a` entry is not protected.
- **Not covered:** the contents of `senc`, `saiz` and `saio` beyond their counts, the data of `pssh` boxes and the FairPlay atoms, PIFF's `uuid` boxes (no source at hand), item protection (`ipro`), and the encryption of video, text or metadata tracks, which the analyzer does not read.

## Apple Lossless

An `alac` sample entry is shown with the container; its samples are not analyzed yet (`mp4.codec-not-analyzed`, phase 3).

## Channel layout

The codec's layout counts. The chan and chnl boxes complete a stream whose codec states no layout.

## Encoder detection

The container's metadata (©too, QuickTime's ©swr) names an encoder as written, with low confidence. Encoding Params and iTunSMPB are shown but name no encoder.

Apple's AudioToolbox is named from the layout of its files (see [AAC](aac.md)):

- the DecoderConfigDescriptor's stream type byte has its reserved bit clear (0x14, shown as "reserved bit 0"), where ISO/IEC 14496-1 sets it;
- with an sbtd box (the source bit depth, shown as a box of its own) in the audio sample entry, the encoder is named, "Apple AAC encoder (AudioToolbox)", with medium confidence: the box records the PCM source of an encode;
- with a free box before mdat that puts the audio data on a 4,096-byte boundary instead, the writer is named, "Apple AudioToolbox", with low confidence.

Apple's writer adds the sbtd box to M4A and M4B files, not to MP4 or 3GP files. Sound Check adds a `udta/ludt/tlou` loudness box to the track, shown as an unread box, and a freeform iTunNORM item. A remux by other software drops the signs. The layout is the writer's: afconvert's `-d 0` copies the packets of another file as they are (run 4), and the writer gives every copy the padding, and the stream type byte 0x14 when the source is ADTS (an fdkaac stream copied that way is named Apple at low confidence; ffmpeg's and FAAC's keep their version text and are named by it), but never the sbtd box. An M4A source keeps its own esds, with the stream type byte 0x15, so its copy shows no layout and is not named. afconvert writes an iTunSMPB and no Encoding Params or ©too.

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
- protection: the sinf box of a protected sample entry (frma, schm, schi with tenc), pssh, senc, saiz and saio boxes, the `seig` sample group, and the entries and boxes of iTunes protection and Audible (see "Protected tracks").

Each audio track's samples go to its codec's parser: [AAC](aac.md), [MPEG audio](mpeg-audio.md) for MP3 in MP4, or [AC-3 and E-AC-3](ac3.md) or [DTS](dts.md).

- **MPEG audio is read only where its samples lie in one stretch** of the file (the MPEG audio parser reads the bytes as it would in a file). A track whose samples lie in several stretches is still a stream, described by its sample entry and its sample tables ("frames not read: samples not in one stretch"), and the MPEG audio parser says its packets are not analyzed (`mpeg.packets-not-analyzed`).
- **The track's handler** is the one in its `mdia` box (ISO/IEC 14496-12 8.4.3); the handler of a `meta` box in the `trak` box is the metadata's.
- **Boxes where they do not belong** are shown and reported (`mp4.box-misplaced`): a `moov` box inside another box is not the movie, a `traf` box inside a `traf` box is a track fragment of its own whose protection evidence counts for every track, and a `moof` box inside a `moof` box is a fragment of its own. The tracks of a second `moov` box (`mp4.duplicate-box`; players read the first) are shown and not analyzed.
- **User data lists** may end in a 32-bit zero (QuickTime File Format, "User data list"); it is shown as a terminator, without a warning.

## Limits

The work and the memory the analysis takes are bounded by the file, not by what its boxes state:

- **Samples** of a valid file do not overlap, so the bytes of the samples that the sample tables and the track fragments place add up to at most the file's length. The fragments draw on that budget as they are read and each track's sample tables when the track is routed; where it runs out, the samples after that are not analyzed and a finding says so (`mp4.samples-overlap`). Chunks that share bytes, or runs that point back to the same data, reach it quickly.
- **Fragment samples** of audio tracks are kept up to 4,194,304 for the whole file, not per track (the cap counts audio samples only; `mp4.fragment-samples-limit`). Past it they are counted and never kept: their number, bytes and durations are added to the stream's samples, duration and average bitrate. Samples of no bytes are counted and not kept (`mp4.empty-samples`).
- **Nodes** of the tree are limited to 250,000 in all, the boxes' and those of their fields and entries. Boxes that start past the limit are still read, into compact records, for the tracks, samples, fragments, durations and protection evidence they describe, so a valid file is read in full however many fragments it has; they make no nodes, and the rest of their parent is shown as one "Boxes Not Shown" node with a finding (`mp4.node-limit`). The checks of the children of a box that is not shown (required and duplicate boxes) are not made.
- **Long lists** (edits, channel descriptions, table entries) show their first 1,000 entries as nodes and count the rest ("More Entries").

## Findings

- broken box sizes and inconsistent sample tables;
- samples outside the file or outside mdat, and samples that overlap;
- boxes where they do not belong (a moov inside another box, a traf in a traf, a moof in a moof) and more boxes than the limit;
- durations that disagree;
- moov after mdat;
- an edit list and iTunSMPB that disagree on the encoder delay or padding, or an iTunSMPB whose total is not the track's duration (Apple Lossless's duration leaves out the padding, which is accepted);
- a chan or chnl layout that disagrees with the codec's.

## Protected tracks

A protected track is reported on its stream (`StreamInfo.Protection`: the scheme, whether the samples are encrypted and the DRM systems the file names), in the JSON as `isProtected` and `protection`, in the CSV and in the GUI. Nothing is decrypted, and no key or license is read or fetched.

- **Sample entries:** `enca` and `encv` (and the other `enc*` entries) are read as the original format that their `sinf` box names in `frma`, so an `enca` entry of AAC, AC-3, E-AC-3, DTS or MPEG audio is routed to that codec. `drms` and `drmi` (the entries of iTunes purchases) and `aavd` (Audible) are protected whatever they contain; `aavd` has the layout of `mp4a`, as GStreamer's `qtdemux` reads it.
- **sinf:** the original format (`frma`), the scheme (`schm`: type, version and URI) and the scheme's data (`schi`). A protected entry without `sinf`, `frma` or `schm` is a finding (`mp4.protected-entry-no-sinf`, `-no-frma`, `-no-schm`); without `frma` the coding is unknown and the samples are not analyzed.
- **Common Encryption** (ISO/IEC 23001-7): the schemes `cenc`, `cens`, `cbc1` and `cbcs`, named from the MP4 registration authority's list; `tenc` (default IsProtected, per-sample IV size, KID, the crypt and skip blocks of version 1, a constant IV) with findings for an IV size that is not 0, 8 or 16 (`mp4.tenc-iv-size`) and a protected track with no usable constant IV (`mp4.tenc-constant-iv`), as GPAC and FFmpeg check them; `pssh` (versions 0 and 1, the system ID named from DASH-IF's registry, the key IDs and the data size); `senc` (flags and the number of samples), `saiz`, `saio` and `seig` sample groups. A `cenc`-family scheme without `tenc` is a finding (`mp4.protected-entry-no-tenc`).
- **Sample entries of a track:** the descriptions the track's samples use decide: `stsc` for the chunks the chunk offset box has, and `tfhd` or `trex` in fragments. A track that uses any protected entry is a protected stream, described through the first of them that encrypts; an index that points to no entry, or a track fragment without a `tfhd` box, may be any protected entry.
- **Encrypted samples:** a track counts as encrypted unless its key information says no sample is protected and nothing in the file says otherwise. Encspect does not prove sample by sample that encrypted tracks are partly clear. It counts a protected track as clear only if all of this holds:
  - every protected entry in use is a Common Encryption entry whose `sinf` boxes agree on a known scheme, name the original format and give `tenc` boxes that all set the default to not protected (the entries of `drms`, `drmi` and `aavd`, a scheme the project does not know, a missing `sinf` or `tenc`, or two `tenc` boxes that differ never qualify);
  - no `seig` sample group description protects, wherever it lies in the track or its fragments, in version 0, 1 or 2 of the box; one that cannot be read, or of a later version, counts as protecting;
  - there is no `senc` box, and no `saiz` or `saio` box of an encryption type or without a type, in the track or its fragments; one of these boxes that ends before the fields that say how it is laid out counts as present, and so does a `saiz` or `saio` box whose type cannot be read.

  Evidence counts wherever it lies in the file: a `tenc` box that protects, a `senc`, `saiz` or `saio` box as above, a `seig` description that protects, a `sinf` or `schm` box, and PIFF's sample and track encryption boxes (`uuid` boxes, named as GPAC names them). A sample entry that carries a `sinf` box is a protected entry whatever its type. Evidence inside a trak box, or inside a track fragment that names exactly one existing track, is that track's; evidence anywhere else (the top level, moov, a track fragment that names no track, a missing track or more than one, a traf or moof inside a trak box) is every track's, and so is the rest of a file whose boxes stop before its end (an unknown or damaged top-level box), which is also a finding. A track fragment with more than one `tfhd` box is a finding and its runs are not placed.
  A track whose `tenc` protects by default is encrypted even if its sample groups put every sample in a clear group (MP4Box's `selectiveType="Clear"` writes such a file). The samples of an encrypted track are never handed to a codec parser: AAC, AC-3, E-AC-3 and DTS read the configuration of the sample entry (`esds`, `dac3`, `dec3`, `ddts`) without packets, and the stream shows "frames not read: encrypted"; for MPEG audio, which has no configuration, the container describes the stream from the sample entry. The duration, samples and average bitrate come from the sample tables. An info finding says so (`mp4.samples-encrypted`).
- **Sample groups:** `sgpd` and `sbgp` boxes are shown wherever they are, but they belong in `stbl` or `traf` (as GPAC reads them), and elsewhere they are a finding (`mp4.sample-group-misplaced`). A `sgpd` box is read as FFmpeg's `mov_read_sgpd` and GPAC read it: the default length from version 1, the default description index from version 2, and an entry length where the default length is 0; in version 0 a `seig` entry sizes itself. The runs and defaults are shown, not used to decide.
- **Clear protected tracks:** a Common Encryption track whose key information says no sample is protected, with nothing in the file that says otherwise, is read like any other, the stream is protected but not encrypted, and an info finding says so (`mp4.protected-samples-clear`). A run of `stsc` for chunks the chunk offset box does not have is a finding (`mp4.stsc-chunk-past-end`) and its sample description is not in use.
- **iTunes protection:** the `sinf` box of a `drms` entry holds `frma` and the atoms `user`, `key `, `iviv` and `priv` in `schi`. A source lists them (VLC's `libmp4.c` skips them as the DRM atoms of a `drms` entry; MediaInfo reports the encryption as iTunes), but none describes their contents, so they are shown by size.
- **Audible:** the `adrm` box of an `aavd` entry is split as FFmpeg's `mov_read_adrm` reads it: 8 bytes, a 56-byte blob, 4 bytes and a 20-byte checksum. A shorter box is a finding (`mp4.adrm-short`).
- **iTunes purchases:** the account (`apID`), purchase date (`purd`), owner (`ownr`) and store IDs are metadata items of the iTunes tag, never protection; a file with them and an ordinary `mp4a` entry is not protected.
- **Not covered:** the contents of `senc`, `saiz` and `saio` beyond their counts, the data of `pssh` boxes and the atoms of iTunes protection, PIFF's `uuid` boxes (no source at hand), item protection (`ipro`), and the encryption of video, text or metadata tracks, which the analyzer does not read.

## Apple Lossless

An `alac` sample entry is shown with the container; its samples are not analyzed yet (`mp4.codec-not-analyzed`, phase 3).

## Channel layout

The codec's layout counts. The chan and chnl boxes complete a stream whose codec states no layout.

## Encoder detection

The container's metadata (©too, QuickTime's ©swr) names the writer as written (a ©too item counts only if its data atom is of a text type: UTF-8, UTF-16, S/JIS or their sort variants), with low confidence: QuickTime defines ©swr as the software that generated the movie, and no Apple specification defines ©too, so both go to the writer, and the encoder comes from the stream. Text that is ffmpeg's muxer version (`Lavf…`) is the writer in any case, and a libavcodec encoder ID (`Lavc…`) in either atom is an encoder claim. Encoding Params and iTunSMPB are shown but name neither. An ID32 box's TSSE frame is the encoder's, as in any ID3v2 tag.

Apple's AudioToolbox is named from the layout of its files (see [AAC](aac.md)):

- the DecoderConfigDescriptor's stream type byte has its reserved bit clear (0x14, shown as "reserved bit 0"), where ISO/IEC 14496-1 sets it;
- with an sbtd box (the source bit depth, shown as a box of its own) in the audio sample entry, the encoder is named, "Apple AAC encoder (AudioToolbox)", with medium confidence: the box records the PCM source of an encode;
- with a free box before mdat that puts the audio data on a 4,096-byte boundary, the writer is named, "Apple AudioToolbox", with low confidence, whether or not there is an sbtd box.

Apple's writer adds the sbtd box to M4A and M4B files, not to MP4 or 3GP files. Sound Check adds a `udta/ludt/tlou` loudness box to the track, shown as an unread box, and a freeform iTunNORM item. A remux by other software drops the signs. The layout is the writer's: afconvert's `-d 0` copies the packets of another file as they are (run 4), and the writer gives every copy the padding, and the stream type byte 0x14 when the source is ADTS (an fdkaac stream copied that way gets the writer Apple at low confidence and no encoder; ffmpeg's and FAAC's keep their version text and are named by it as the encoder, with the writer Apple next to it), but never the sbtd box. An M4A source keeps its own esds, with the stream type byte 0x15, so its copy shows no layout and is not named. afconvert writes an iTunSMPB and no Encoding Params or ©too.

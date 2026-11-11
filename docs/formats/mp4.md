# MP4

MP4, M4A, M4B, 3GP and QuickTime files.

## What is read

Box by box:

- every box's header;
- the movie, track and media headers;
- sample descriptions, with their esds box and QuickTime's sound descriptions;
- the sample tables, edit lists, roll groups and movie fragments;
- iTunes metadata with every data type, iTunSMPB, Encoding Params, an ID32 box's ID3v2 tag and Nero's chapters;
- QuickTime's user data text atoms (the © atoms of `udta`), in a user data tag: see "QuickTime user data text";
- QuickTime's chan atom (Core Audio's layout tags, channel bitmap and channel labels) and the ISO chnl box;
- protection: the sinf box of a protected sample entry (frma, schm, schi with tenc), pssh, senc, saiz and saio boxes, the `seig` sample group, and the entries and boxes of iTunes protection and Audible (see "Protected tracks").

**The boxes in detail:**

- **Headers:** the 32-bit size, the 64-bit size after a size of 1, size 0 to the end of its parent, a uuid box's user type, and a full box's version and flags. The file's boxes sit under one MP4 node; bytes after the last box are trailing data.
- **The run of top-level boxes** ends at the first type ISO/IEC 14496-12 does not place at the top level, which is also the end `DeclaredEnd` gives.
  - A trailing ID3v1 or APE tag after the boxes is the file's; a `TAG` inside the last box is the container's (a near miss).
  - A last box of size 0 declares no end.
- **Movie and track:** ftyp and styp; moov, mvhd, trak, tkhd, edts and elst, mdia, mdhd (with its packed language), hdlr (QuickTime's component type and Pascal string name too), minf, smhd, dinf and dref.
- **Sample tables:** stbl with stsd and its sample entries, stts, ctts, stsc, stsz, stz2, stco, co64, stss, sgpd (roll and pre-roll distances) and sbgp. They are read a block at a time into arrays of the entries the box holds, never more, whatever count it declares.
- **Sample entries:** the audio sample entry with esds and its ES_Descriptor, DecoderConfigDescriptor (object type, bitrates) and DecoderSpecificInfo, and btrt.
  - QuickTime's sound sample descriptions of version 1 and 2, with their wave box, are read where the file is QuickTime's (without ftyp, or with the brand `qt  `) or the stsd box is of version 0, as ffmpeg reads them.
  - ISO/IEC 14496-12's own version 1 keeps version 0's layout.
- **Fragments:** mvex, mehd, trex, moof, mfhd, traf, tfhd, tfdt, trun, sidx, mfra, tfra and mfro.
- **Metadata boxes:** udta with QuickTime's international text atoms, meta (a full box, or QuickTime's plain container), keys, ilst, ID32 and Nero's chpl.

**Metadata:**

- **The tag:** an item list is a tag of the container, a Tag node named "iTunes Metadata"; under an `mdta` handler it is "QuickTime Metadata", and its items are named by their keys.
- **Items** are named for what they are ("Title", "Track Number"); a freeform item ("----") by its name atom.
- **Data types:** each data atom's value is typed by its well-known type: UTF-8, UTF-16, S/JIS, signed and unsigned integers of every width, floats, point, rectangle, images. The implicit type of track and disc numbers and of the ID3v1 genre is decoded.
- **iTunSMPB:** its hexadecimal words are read into the encoder delay, padding and samples, each a node at its characters.
- **Encoding Params:** read into its pairs of key and value: vers, acbf (Core Audio's bitrate control modes CBR, ABR, constrained VBR and VBR), brat, cdcv.
- **ID32:** its ID3v2 tag goes to the ID3v2 tag parser through `ParseTag`, so its TSSE frame reaches the codec as a claim.

**Routes:**

- **AAC:** object types 0x40 and 0x66 to 0x68 go to "aac", with the DecoderSpecificInfo as the `CodecConfiguration`.
- **Bitrate mode:** whether the bitrate varies comes from Encoding Params, else from an avgBitrate of 0 (ISO/IEC 14496-1 7.2.6.6.2).
- **Where the samples node goes:** into the mdat box that holds all the track's samples, or with its ranges into the MP4 node where they span boxes.

Each audio track's samples go to its codec's parser: [AAC](aac.md), [MPEG audio](mpeg-audio.md) for MP3 in MP4, [AC-3 and E-AC-3](ac3.md), [DTS](dts.md) or [Apple Lossless](alac.md). Other codings, among them FLAC (`fLaC`), Opus and Dolby TrueHD (`mlpa`), are shown as boxes with `mp4.codec-not-analyzed`.

- **Units counted:** a sample counts when its first byte is in the file; one the file ends inside is handed to the codec parser with the bytes present and flagged as cut (`MappedBytes.IsCut`), so a raw AAC access unit of 1,024 samples counts them and is also reported as lost samples; a sample with no byte in the file or of 0 bytes counts nothing.
- **MPEG audio is read only where its samples lie in one stretch** of the file (the MPEG audio parser reads the bytes as it would in a file). A track whose samples lie in several stretches is still a stream, described by its sample entry and its sample tables ("frames not read: samples not in one stretch"), and the MPEG audio parser says its packets are not analyzed (`mpeg.packets-not-analyzed`).
- **Playback groups:** the audio tracks of a movie play at the same time, so every stream that comes from a track has the movie's playback group ([architecture.md](../architecture.md#streams-and-series)).
- **The track's handler** is the one in its `mdia` box (ISO/IEC 14496-12 8.4.3); the handler of a `meta` box in the `trak` box is the metadata's.
- **Boxes where they do not belong** are shown and reported (`mp4.box-misplaced`): a `moov` box inside another box is not the movie, a `traf` box inside a `traf` box is a track fragment of its own whose protection evidence counts for every track, and a `moof` box inside a `moof` box is a fragment of its own. The tracks of a second `moov` box (`mp4.duplicate-box`; players read the first) are shown and not analyzed.
- **User data lists** may end in a 32-bit zero (QuickTime File Format, "User data list"); it is shown as a terminator, without a warning.

- **The Gapless node** under the trak box holds what the edit list (the first media edit's media time, the presented samples and the padding after them), the roll and pre-roll groups and iTunSMPB say, counted in the media's timescale: the sample rate for AAC LC, and the core's rate as fdkaac writes HE-AAC.
- **Container Duration** and **Container Samples** under a track's "Gapless" node: the media duration of the mdhd box (ISO/IEC 14496-12, 8.4.2) in seconds, and in samples at the stream's output sample rate (the timescale of an HE-AAC track is usually the core rate, so its figure is twice the mdhd duration in timescale units; nothing for a fragmented file whose mdhd states 0); the tkhd duration and the edit list are other figures.

## Limits

The work and the memory the analysis takes are bounded by the file, not by what its boxes state:

- **Samples** of a valid file do not overlap, so the bytes of the samples that the sample tables and the track fragments place add up to at most the file's length. The fragments draw on that budget as they are read and each track's sample tables when the track is routed; where it runs out, the samples after that are not analyzed and a finding says so (`mp4.samples-overlap`). Chunks that share bytes, or runs that point back to the same data, reach it quickly.
- **Fragment samples** of audio tracks are kept up to 4,194,304 for the whole file, not per track (the cap counts audio samples only; `mp4.fragment-samples-limit`). Past it they are counted and never kept: their number, bytes and durations are added to the stream's samples, duration and average bitrate. Samples of no bytes are counted and not kept (`mp4.empty-samples`).
- **Nodes** of the tree are limited to 250,000 in all, the boxes' and those of their fields and entries. Boxes that start past the limit are still read, into compact records, for the tracks, samples, fragments, durations and protection evidence they describe, so a valid file is read in full however many fragments it has; they make no nodes, and the rest of their parent is shown as one "Boxes Not Shown" node with a finding (`mp4.node-limit`). A box with a hidden child skips the check for required boxes (the one missing may be among the hidden), while repeated boxes among the shown children are still found; boxes that are not shown are not checked. Findings about hidden boxes, and the codec's and tags' nodes made from them, point to the "Boxes Not Shown" node of the parent where they were hidden, and the nodes that a codec parser or a tag parser makes from them (a codec configuration, an ID32 tag) are placed under that node.
- **Long lists** (edits, channel descriptions, table entries) show their first 1,000 entries as nodes and count the rest ("More Entries").

## Findings

- broken box sizes and inconsistent sample tables:
  - a box that declares more bytes than its parent holds is read up to the parent's end with a warning;
  - one whose size is smaller than its header ends the reading of its parent's boxes;
  - boxes nest at most 40 deep (a warning), so the tree stays within the nesting limit with the fields and the codec's nodes under the deepest box;
  - stts and stsz counting different samples, stsc's order and sample description indexes, and chunks that hold fewer or more samples than stsz sizes;
- required boxes missing or present twice, and ftyp not first;
- edit lists with empty edits, more than one media edit, a rate other than 1, a media time below -1 or media past its end;
- samples outside the file or outside mdat, samples the file ends inside (reported with the samples outside it, `mp4.sample-outside-file`), and samples that overlap;
- boxes where they do not belong (a moov inside another box, a traf in a traf, a moof in a moof) and more boxes than the limit;
- durations that disagree: mdhd's duration against the sum of stts's durations, tkhd's against the edit list or the media (ISO/IEC 14496-12 8.3.2.3);
- no moov, and moov after mdat (an info: the file does not start playing before it is read to the end);
- an edit list and iTunSMPB that disagree on the encoder delay or padding, or an iTunSMPB whose total is not the track's duration (Apple Lossless's duration leaves out the padding, which is accepted);
- a chan or chnl layout that disagrees with the codec's.
- a text item of a user data atom that states more bytes than the atom holds (`mp4.text-size-invalid`), and bytes at the end of such an atom that are too few for an item's size and language (`mp4.text-item-truncated`).

## QuickTime user data text

The © atoms of `udta` in `moov` and `trak` are international text (QuickTime File Format, "User data atoms"). A `udta` box that holds at least one of them is the tag: the same box node, of kind Tag, named "QuickTime User Data", so the summary, `--hide tags` and the CSV Tags column see it.

- **The tag:** `--hide tags` hides the whole box, atoms such as `WLOC` or `LOOP` included, since Apple defines all of them as user data. A tag it holds (an item list in `meta`, an ID32 box's ID3v2 tag) is listed in the summary too. The summary lists a tag once for each box that is one, so a movie's and a track's user data give the name twice, as two ID3v2 tags do.
- **Which atoms:** the ones the File Format's table lists (`©arg`, `©ark`, `©cok`, `©com`, `©cpy`, `©day`, `©dir`, `©ed1` to `©ed9`, `©fmt`, `©inf`, `©isr`, `©lab`, `©lal`, `©mak`, `©mal`, `©nak`, `©nam`, `©pdk`, `©phg`, `©prd`, `©prf`, `©prk`, `©prl`, `©req`, `©snk`, `©snm`, `©src`, `©swf`, `©swk`, `©swr`, `©wrt`); `©ART`, `©alb`, `©cmt` and `©des`, which its appendix on digital audio file formats names for the MP3 importer (it puts the ID3v1 track number in `©des`); and `©too`, which no Apple specification defines in user data and which ffmpeg's and MediaInfo's readers take as the encoding tool. Each is named for its field ("Title", "Artist", "Software", "Movie Writer" for `©wrt`, since "Writer" is the software that wrote the file).
- **Other © atoms** are shown as text items, without a field name, where their items fill the atom exactly (every size is read); otherwise they are unread boxes. Structural children of `udta` (`WLOC`, `LOOP`, `meta`, `hnti` and the like) are never read as text.
- **Items:** each atom holds one or more text items of a 16-bit size, a 16-bit language code and the text. The size is the length of the text alone, as ffmpeg and MediaInfo read it. The atom's value is its first non-empty text.
- **Language and encoding:** a code below 0x400, or 0x7FFF, is a Macintosh language code (named from the File Format's table), and its text is decoded as Macintosh Roman, as ffmpeg decodes it. Any other code is three packed ISO 639-2/T letters, and the text is UTF-16 after a byte order mark and UTF-8 otherwise; text that is not valid UTF-8 is read as Latin-1.
- **Damage:** an item whose size is beyond the atom is shown with the bytes that exist and ends the atom (`mp4.text-size-invalid`); fewer than 4 bytes at the end are shown as unused bytes (`mp4.text-item-truncated`).
- **Limits:** at most 1,000 items of an atom, and 10,000 of the whole file, are shown as nodes; the rest are counted in a "More Text Items" node. Atoms in boxes past the node budget build no item nodes at all; their items are only walked for the findings and the first text.
- **Writer:** the first text of `©swr` and `©too` is a claim about the writer (see "Encoder detection"); an item list's `©too` adds one for each text data atom.
- **MediaInfo** reads a movie's `©nam` as a version, flags and one UTF-8 text to the end of the atom, so it matches the analyzer only for an atom of one item; its other fields in `udta` are compared (title, artist, album, encoding application).
- **Not covered:** the atoms of `udta` that are not international text (`name`, `tnam`, `tagc`, `LOOP`, `WLOC` and the like) are shown as boxes and not read. Nero's `tags` box, neroAacTag's own tag format beside the iTunes metadata, is shown as a box and not read; reading it is planned ([plan.md](../plan.md), 0.4).

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

## Reference for sample counts

- **AAC tracks:** the first sound track's samples (`stsz` or `stz2`, `stco` or `co64`, `stsc`, and `moof` and `trun` of fragments) are fed to Fraunhofer's FDK AAC decoder one by one, in file order, with the track's AudioSpecificConfig; its total at the coded stage is the reference for the stream's `samples` (see [aac.md](aac.md) and "AAC decoding by FDK AAC" in [development.md](../development.md)). A sample that is cut or outside the file is fed up to the last byte the file holds.
- **Not fed (108 files of this folder):** tracks with an `enca` sample entry (93) or another protection scheme (6), `aavd` (2), `drms` (1), `alac` (2), MP3 in MP4 (2) and files without `moov` (2). They have no reference here.
- **The gapless nodes are held to Core Audio's `ExtAudioFile`** (rollout step 5, run 41463609030): "Samples (iTunSMPB)" and "Samples (Edit List)" are compared with the reader's total converted to the core rate, the media timescale, at which the analyzer counts both. The reader applies iTunSMPB or the edit list (the edit list where both differ) and agrees with the analyzer in 5 of 6 files of this folder (52 of 52 in `aac`); `edit-list-beyond-unlisted.m4a` is `noted` (the edit list plays 44,100 units past the media's 20,480, `mp4.edit-beyond-media`; the reader reads 18,368, the media less its own default priming). A file with no gapless data reads 2,112 frames of the core rate fewer than its samples (the reader's own priming, `noted`).
- **Core Audio's converter** is fed the same samples as FDK and is the second reference of `samples` (77 totals in this folder, equal to the analyzer's in 62); it refuses every unit of the five hand-built xHE-AAC files and `usac-reserved-rate.m4a` as FDK does, outputs nothing for seven of the eight `eld-ld-sbr` files (a converter is built; `eld-ld-sbr-13.m4a` has none, `-50`, and neither has `chan-completes-reserved.m4a`), and decodes `sample-outside-mdat.m4a` to 11,264 against the analyzer's 20,480 (`noted`, the extra block unexplained).
- **Media Foundation's AAC decoder** (rollout step 6, run 41558366773) is fed the same samples as FDK and is the third decoder of the rule: 67 totals in this folder, equal to the analyzer's in 59 and to FDK's in 61 of 61. It gives no media type for the eight hand-built `eld-ld-sbr` files, `eld.m4a`, `chan-7-1-rear.m4a` (8 channels; Learn gives at most 6) and `chan-completes-reserved.m4a` (`explained` where a documented limit names it, else `noted`), is not asked for `rate-1-hz.m4a` (the probe builds no input type from its explicit sampling frequency), and outputs nothing for the five hand-built xHE-AAC files and `usac-reserved-rate.m4a`. It equals FDK in `truncated.m4a` (10,240, a frame for the cut sample) and `sample-outside-mdat.m4a` (10,240), where Core Audio's converter and the analyzer (`truncated.m4a`) or the converter alone (11,264) differ. The Source Reader applies neither iTunSMPB nor the edit list and is a witness of the coded stage; it reads 65 files to the end and equals the analyzer's `samples` in 56.
- The check is `AacOfficialDecodersTests`; the figures, the run and the differences are in `official-decoders.json` (FDK and the comparison in run 41558366773, Core Audio in the same run and Media Foundation in the same run; see "AAC, AC-3 and E-AC-3 decoding by Core Audio" and "AAC decoding by Media Foundation" in [development.md](../development.md)).

## Apple Lossless

An `alac` sample entry that holds an `alac` box (the magic cookie) goes to the [ALAC](alac.md) codec parser with its samples, which reads them frame by frame. An entry without the box stays at container level (`mp4.codec-not-analyzed`), and an encrypted track hands over the cookie only.

## Channel layout

The codec's layout counts. The chan and chnl boxes complete a stream whose codec states no layout.

- **chan** (QuickTime, Core Audio's AudioChannelLayout): a layout tag named and decoded from Apple's constants in CoreAudioBaseTypes.h, a channel bitmap in the WAVE channel mask's order, or a description per channel with its label, flags and coordinates.
- **chnl** (ISO/IEC 14496-12, version 0): the stream structure, a ChannelConfiguration of ISO/IEC 23091-3 with its omitted channels, or a speaker position per channel of the sample entry, with azimuth and elevation for an explicit one, and the object count.
- In both, Ls and Rs are the back pair unless the layout also has rear surrounds.
- The container's layout completes the codec's only where the channel counts agree. One that names other speakers is a warning (`mp4.channel-layout-differs`), also where a mono AAC core meets a stereo container, since implicit parametric stereo is read from the blocks and a core without it is mono.

## Encoder detection

The container's metadata (©too, QuickTime's ©swr, in an item list or in `udta`) names the writer as written (a ©too item counts only if its data atom is of a text type: UTF-8, UTF-16, S/JIS or their sort variants; the first text of a `udta` atom counts), with low confidence: QuickTime defines ©swr as the software that generated the movie, and no Apple specification defines ©too, so both go to the writer, and the encoder comes from the stream. Text that is ffmpeg's muxer version (`Lavf…`) is the writer in any case, and a libavcodec encoder ID (`Lavc…`) in either atom is an encoder claim. Encoding Params and iTunSMPB are shown but name neither. An ID32 box's TSSE frame is the encoder's, as in any ID3v2 tag.

Apple's AudioToolbox is named from the layout of its files (see [AAC](aac.md)):

- the DecoderConfigDescriptor's stream type byte has its reserved bit clear (0x14, shown as "reserved bit 0"), where ISO/IEC 14496-1 sets it;
- with an sbtd box (the source bit depth, shown as a box of its own) in the audio sample entry, the encoder is named, "Apple AAC encoder (AudioToolbox)", with medium confidence: the box records the PCM source of an encode;
- with a free box before mdat that puts the audio data on a 4,096-byte boundary, the writer is named, "Apple AudioToolbox", with low confidence, whether or not there is an sbtd box.

Apple's writer adds the sbtd box to M4A and M4B files, not to MP4 or 3GP files. Sound Check adds a `udta/ludt/tlou` loudness box to the track, shown as an unread box, and a freeform iTunNORM item. A remux by other software drops the signs. The layout is the writer's: afconvert's `-d 0` copies the packets of another file as they are (run 4), and the writer gives every copy the padding, and the stream type byte 0x14 when the source is ADTS (an fdkaac stream copied that way gets the writer Apple at low confidence and no encoder; ffmpeg's and FAAC's keep their version text and are named by it as the encoder, with the writer Apple next to it), but never the sbtd box. An M4A source keeps its own esds, with the stream type byte 0x15, so its copy shows no layout and is not named. afconvert writes an iTunSMPB and no Encoding Params or ©too.

## Articles

- **Articles:** `iso-base-media-format`, `mp4-sample-tables`, `fragmented-mp4`, `edit-lists-and-priming`, `itunes-metadata`, `quicktime-user-data`.
- **Shared with other formats:** `channel-layouts`, `gapless-playback`, `encoder-detection`, `id3v2`.
- The chan and chnl boxes point to `channel-layouts`, which MPEG audio and AAC share.

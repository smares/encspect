# MP4

MP4, M4A, M4B, 3GP and QuickTime files.

## What is read

Box by box:

- every box's header;
- the movie, track and media headers;
- sample descriptions, with their esds box and QuickTime's sound descriptions;
- the sample tables, edit lists, roll groups and movie fragments;
- iTunes metadata with every data type, iTunSMPB, Encoding Params, an ID32 box's ID3v2 tag and Nero's chapters;
- QuickTime's chan atom (Core Audio's layout tags, channel bitmap and channel labels) and the ISO chnl box.

Each audio track's samples go to its codec's parser: [AAC](aac.md), [MPEG audio](mpeg-audio.md) for MP3 in MP4, or [AC-3 and E-AC-3](ac3.md) or [DTS](dts.md).

## Findings

- broken box sizes and inconsistent sample tables;
- samples outside the file or outside mdat;
- durations that disagree;
- moov after mdat;
- an edit list and iTunSMPB that disagree on the encoder delay or padding, or an iTunSMPB whose total is not the track's duration (Apple Lossless's duration leaves out the padding, which is accepted);
- a chan or chnl layout that disagrees with the codec's.

## Apple Lossless

An `alac` sample entry is shown with the container; its samples are not analyzed yet (`mp4.codec-not-analyzed`, phase 3).

## Channel layout

The codec's layout counts. The chan and chnl boxes complete a stream whose codec states no layout.

## Encoder detection

The container's metadata (©too, QuickTime's ©swr) names an encoder as written, with low confidence. Encoding Params and iTunSMPB are shown but name no encoder.

Apple's AudioToolbox is named from the layout of its files, with medium confidence (see [AAC](aac.md)):

- the DecoderConfigDescriptor's stream type byte has its reserved bit clear (0x14, shown as "reserved bit 0"), where ISO/IEC 14496-1 sets it;
- and an sbtd box (the source bit depth, shown as a box of its own) in the audio sample entry, or a free box before mdat that puts the audio data on a 4,096-byte boundary.

Apple's writer adds the sbtd box to M4A files, not to MP4 or 3GP files. Sound Check adds a `udta/ludt/tlou` loudness box to the track, shown as an unread box, and a freeform iTunNORM item. A remux by other software drops both signs. afconvert writes an iTunSMPB and no Encoding Params or ©too.

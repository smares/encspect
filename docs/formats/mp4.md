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

Each audio track's samples go to its codec's parser: [AAC](aac.md), [MPEG audio](mpeg-audio.md) for MP3 in MP4, or [AC-3 and E-AC-3](ac3.md).

## Findings

- broken box sizes and inconsistent sample tables;
- samples outside the file or outside mdat;
- durations that disagree;
- moov after mdat;
- an edit list and iTunSMPB that disagree on the encoder delay or padding;
- a chan or chnl layout that disagrees with the codec's.

## Channel layout

The codec's layout counts. The chan and chnl boxes complete a stream whose codec states no layout.

## Encoder detection

The container's metadata (©too, QuickTime's ©swr) names an encoder as written, with low confidence. Encoding Params and iTunSMPB are shown but name no encoder.

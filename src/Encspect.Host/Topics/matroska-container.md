---
id: matroska-container
title: The Matroska and WebM container
related: ebml, matroska-lacing, matroska-tracks, matroska-timestamps, crc
references: RFC 9559 sections 4, 5, 6, 22 and 23; the WebM Container Guidelines (webmproject.org/docs/container); FFmpeg libavformat/matroskadec.c
---

**The problem.** A file that holds video, several audio tracks, subtitles, chapters and pictures must be playable while it downloads, seekable, and editable without rewriting everything. Matroska is an open container that does this, and WebM is the subset of it that the WebM project allows: VP8, VP9 and AV1 video, Vorbis and Opus audio, and WebVTT.

**Structure.** After the [EBML](ebml) header comes one Segment (several, if documents are joined). Its children are the Top-Level Elements: a SeekHead that lists where the others start, Info (the muxing application, the timestamp scale, the duration), Tracks, Cues (an index of seek points), Chapters, Attachments, Tags, and the Clusters. Each Cluster holds a Timestamp and the media as SimpleBlocks and BlockGroups; a block carries one or more frames of one track ([lacing](matroska-lacing)). Void elements pad the header so a writer can add tags or cues later without moving the Clusters, and a CRC-32 element may lead each Top-Level Element.

**Live streams.** A writer that does not know the length in advance gives the Segment and the Clusters the unknown size; each Cluster then ends where the next Top-Level Element starts. A stream with neither a SeekHead nor Cues at its start is taken to be non-seekable.

**What you see.** The tree of elements with their bytes, a Cluster's blocks with their tracks, timestamps and frames, and the frames of each audio track handed to the codec's parser. Findings report sizes that run past the file, missing required elements, SeekHead and Cues positions that point nowhere, and blocks that name a track no TrackEntry describes.

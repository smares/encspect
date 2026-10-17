---
id: matroska-timestamps
title: Timestamps in Matroska
related: matroska-container, matroska-lacing, matroska-tracks, opus-pre-skip, gapless-playback
references: RFC 9559 sections 5.1.2.9, 5.1.3.1, 5.1.4.1.15, 10.1 and 11
---

**The problem.** Timestamps should be small, so each block can state its own in two bytes, and exact, so tracks stay in sync. Matroska counts in ticks whose length the file states.

**Ticks.** The Info element's TimestampScale is the length of a tick in nanoseconds, 1,000,000 unless the file says otherwise, so a tick is a millisecond. A Cluster's Timestamp is an absolute time in ticks. A block's timestamp is a signed 16-bit number of ticks relative to its Cluster, so a block can lie 32.7 seconds before or after the Cluster's start at the default scale. The time of a block is therefore (Cluster Timestamp + block timestamp times TrackTimestampScale) times the scale (section 11.2). The TrackTimestampScale is a track's own factor, 1.0 unless the TrackEntry states one, and it applies to the block's timestamp, which is in Track Ticks, and not to the Cluster's, which is in Segment Ticks; the specification notes that many readers ignore it. Info's Duration is a float in ticks.

**Delay and laced frames.** A track's CodecDelay, in nanoseconds, is subtracted from every block timestamp of the track; a frame whose time falls below zero is decoded but not played. For Opus the delay is the pre-skip. Of the frames of a laced block only the first has a timestamp, and the others follow without a gap.

**Order.** Audio frames are stored in time order, so a block whose time is earlier than the block before it on the same track is a finding. Video with reordered frames may go back in time; the WebM guidelines ask that all timestamps increase.

**What you see.** The Cluster's Timestamp and every block's relative timestamp at their bytes, the track's CodecDelay, and findings for a TimestampScale of zero, timestamps that go back, and a WebM file whose scale is not 1,000,000.

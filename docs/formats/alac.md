# ALAC (Apple Lossless)

The frames of an Apple Lossless track that a container stores: MP4 and M4A (`alac` sample entries) and Matroska (`A_ALAC`), through the "alac" codec parser (`src/Encspect.Formats.Alac`). ALAC has no file format of its own for the analyzer to open: CAF files, which Apple's tools write, wait for a CAF parser. There is no decoder: the frames are walked, not decoded, and the format stores no checksum over its samples ([decoders.md](../design/decoders.md), decision 5).

Sources: Apple's ALAC reference implementation (https://github.com/macosforge/alac, commit `c38887c5c5e64a4b31108733bd79ca9b2496d987`, Apache License 2.0) and its `ALACMagicCookieDescription.txt`. The walk follows `ALACDecoder.cpp` and `ag_dec.c` and carries Apple's notice (`THIRD-PARTY-NOTICES.md`). FFmpeg 6.1.1 and 8.0.1, MediaInfo 26.01 and Apple's `afconvert` are black boxes: they write and read the test files, and FFmpeg's `alac.c` was not read ([decoders.md](../design/decoders.md), decision 10).

## What is read

- The magic cookie, from the `alac` box in the sample entry (MP4) or the CodecPrivate (Matroska):
  - the 24-byte ALACSpecificConfig: frame length, compatible version, bit depth, the Golomb parameters `pb`, `mb` and `kb`, channels, `maxRun`, the largest frame in bytes, the average bitrate and the sample rate;
  - the 24-byte channel layout info that follows it for more than two channels (a Core Audio layout tag, which the report names);
  - the older forms: the same inside an `alac` atom of 36 bytes, and with a `frma` atom before it and a terminator atom after it.
- Every frame (one packet each), walked to the bit:
  - the elements (single channel, channel pair, LFE, data stream, fill and end) with their instance tags and their 12 reserved bits;
  - the header flags: a partial frame's sample count, the bytes shifted out of every sample, and the escape flag (samples stored uncompressed);
  - for a compressed element, `mixBits` and `mixRes`, and for each channel the prediction type, the filter shift, the adaptation factor and the coefficients;
  - the residuals, walked with the adaptive Golomb code of `ag_dec.c` (`Residuals` in `FrameWalker.cs`) to find where each element ends: the long prefix (a value stored whole) and the zero runs are counted. The walk must consume exactly the packet's bytes; the reference decoder stops after the cookie's channels and needs no end tag.
- Limits: the report keeps at most 2,097,152 frames as records (`Records.MaxEntries`), 4,096 of them with their elements as nodes and 64 elements to a frame; the rest are counted (`alac.entries-limit`) and still walked. Large files use compact records, not one node per field.

## Reported

- Nodes: the cookie and its fields (with the layout the tag names), then each frame with its elements and their fields as nodes with bit positions, each with a description and, where one exists, a background article (`alac`, `alac-prediction`, `alac-adaptive-golomb`, `mid-side-stereo`, `wasted-bits`, `channel-layouts`).
- Stream: codec "Apple Lossless", sample rate, channels (with the layout where the cookie states one), bits per sample, samples (the sum of the frames' samples), duration, average and per-frame bitrate with its distribution, and the bitrate mode (variable where the frame bitrates differ, `IsVbr`).
- Statistics: the frames, partial and uncompressed frames, the elements by kind, channel pairs and the pairs that are mixed, and tallies of the prediction types, filter orders, shifts, mixing weights and shifted bytes, the zero runs and long prefixes, and the bytes of fill and data stream elements.
- Series: the bitrate per frame and per second, and category series of the element kinds and of the pair coding (mixed or not) per frame, each category named by the tally entry it counts.

## Verification

ALAC stores no checksum and has no decoder, so no figure is compared. The stream's verification is "no decoder" and unknown; `--verify` adds the info `decode.no-decoder`. The test files' samples are checked outside the analyzer: `crosscheck_alac_fixtures.py` decodes every undamaged fixture with Apple's `alacconvert` and with FFmpeg and compares the hash of each channel with the encoder's input ([development.md](../development.md)).

## Channel layout

- The cookie states a channel count (1 to 8) and, for more than two channels, a Core Audio layout tag. ALAC's eight tags are named with their speakers (`100<<16|1` mono, `101<<16|2` left/right, `113<<16|3` center, left, right, and so on); other tags go through the shared Core Audio table. A layout that describes another count than the cookie is `alac.channel-layout-differs` and the report gives the count only.
- The elements per channel count are Apple's: 1 is a single channel element, 2 a pair, 3 a single and a pair, 4 a single, a pair and a single, 5 a single and two pairs, 6 a single, two pairs and a single, 7 one more single, 8 a single and three pairs and a single. The walk does not require this order. An LFE element (tag 3) is read as a single channel element.

## Routes

- MP4: a track whose sample entry is `alac` and holds an `alac` box (the cookie) is handed to the codec parser with the cookie and the samples, also when it is protected and encrypted (the frames are then not read: `AreSamplesEncrypted`). An entry with no `alac` box has no cookie to hand over and stays at container level (`mp4.codec-not-analyzed`).
- Matroska: `A_ALAC` with its CodecPrivate. A track without one is `alac.cookie-missing` and its frames are counted, not read.
- A payload handed in one piece is `alac.payload-not-analyzed`: a frame is a packet.

## Damage

- The cookie: shorter than the configuration (`alac.cookie-too-short`), a compatible version other than 0 (`alac.compatible-version`), a frame length of 0 or above 1,048,576 samples (`alac.frame-length-invalid`), a bit depth other than 16, 20, 24 and 32 (`alac.bit-depth-invalid`), channels outside 1 to 8 (`alac.channels-invalid`), a layout of another channel count (`alac.channel-layout-differs`), bytes after what the description defines (`alac.cookie-extra`, Info), no cookie in Matroska (`alac.cookie-missing`). Where a field makes a frame unreadable the report says so and does not read the frames.
- The frames:
  - the walk runs out of the packet (`alac.frame-overrun`) or stops before its end (`alac.frame-underrun`), or the last channel has no end tag (`alac.end-missing`);
  - an element tag with no syntax (`alac.element-unknown`: the program and coupling channel elements), a header the decoder refuses (`alac.element-invalid`: 3 shifted bytes or a sample width of no bits or above 32), reserved bits that are not 0 (`alac.element-reserved-bits`), a compressed element with a maximum Golomb parameter `kb` that defines no code (`alac.tuning-invalid`), a run of zeros past the element's samples (`alac.residual-invalid`);
  - more or fewer channels than the cookie states (`alac.channels-exceed`, `alac.channels-missing`), elements of one frame with different sample counts (`alac.element-samples-differ`), a partial frame longer than the frame length (`alac.partial-frame-too-long`);
  - an uncompressed element that also states shifted bytes, which the decoder ignores (`alac.escape-shifted`, Info).
- Anything the reference decoder refuses ends the walk of that frame; the rest of the file is read. Findings carry a count and point to the first element they occurred in.
- No input throws: the walk stops with a fault and the finding says where.

## Encoder detection

- From the containers' claims only: the MP4 writer's tool atom and Matroska's WritingApp and MuxingApp, weighed by the host's writer detection with low confidence. Two claims that name different software are `alac.writer-claim-differs`, and software known to write another format is `alac.writer-other-format`.
- The frames name no encoder and the analyzer applies no rule to them. Observed in Encspect's test files, and not used by the analyzer: Apple's `afconvert` and `alacconvert` write filters of 4 and 8 coefficients only, with prediction type 0, and `maxRun` 255; `alacconvert` leaves `maxFrameBytes` and the average bitrate at 0, `afconvert` states them; FFmpeg 6.1.1's encoder writes filters of 4 to 6 coefficients at its default compression level, `maxRun` 0, a `maxFrameBytes` of the raw frame's size plus 4 (16,388 for 16-bit stereo) and the average bitrate of the input's PCM, and at compression level 0 stores every frame uncompressed with `pb`, `mb` and `kb` of 0. FFmpeg's encoder takes 1, 2, 4, 5, 6 and 7 channels (it mixes 3 down to two and 8 down to seven) and 16-bit and 24-bit input (a 32-bit input becomes a 24-bit stream).

## Not covered

- CAF files, and any decoded view of the samples: no waveform, spectrum or lowpass for ALAC, and no check of the audio.
- The program and coupling channel elements (tags 5 and 2), which the reference decoder refuses.
- Channel layouts other than the cookie's.

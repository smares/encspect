---
id: tak-presets
title: TAK presets, profiles and evaluation levels
related: tak-metadata, encoder-detection, bitrate-modes
references: TAK 2.3.3 applications readme (Takc's options) and the SDK's documentation (Ttak_str_EncoderInfo, Ttak_str_MetaEncoderInfo); Takc.exe 2.3.3 as a black-box writer of the files in Encspect's tests
---

**The problem.** A player or a hardware decoder needs to know how much processing and memory a file asks for, and a person comparing files wants to know how hard the encoder worked.

**How it works.** A preset is a profile from 0 to 4 (fastest to strongest) and an evaluation level: Standard, Extra or Max. The profile fixes what the decoder needs (the frame length, the number of predictors, whether channel decorrelation and the prefilter are used); the evaluation level changes only how the encoder searches, not the decoding. Takc writes them "-p2", "-p2e" and "-p2m", the form the report uses. The stream info stores the profile, and the encoder info the version, the preset and the evaluation. The SDK says the profile equals the preset only for the preset's standard settings: a Turbo preset with 128 predictors stores the High profile. A file without an encoder info object (TAK 1.0) has no evaluation level; the SDK tells applications to show it as unknown.

**What the files show.** In Encspect's test files (Takc 2.3.3, 0.25 s of stereo at 44.1 kHz) the preset and the profile agree at all fifteen presets, and the evaluation level changes the file's size and nothing in the stream info. The frame size follows the profile: 4096 samples at -p0 and -p1, 125 ms (5512 samples) at -p2, 8192 samples at -p3 and 250 ms (11,025 samples) at -p4. At -p2 a 96 kHz and a 192 kHz file store 8192 samples, and Takc refuses a frame size limit of 16,384 at -p2 ("FrameSizeLimit too high for preset"). The analyzer reports what the file stores and uses none of this to guess a setting.

**What you see.** The codec, profile, preset and frame length in the statistics, and the stored fields in the objects.

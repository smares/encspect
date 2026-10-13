# AC-3 and E-AC-3

Dolby Digital and Dolby Digital Plus, as raw files and in [MP4](mp4.md).

## What is read

- **Frames:** sync on a chain of frames, with resync after damage.
- **CRCs:** crc1 and crc2.
- **Bit stream information:** every field.
- **Audio blocks:** every block up to its mantissas. The parser steps over the mantissas by running the bit allocation itself; liba52 confirms where every block of the AC-3 fixtures ends.
- **E-AC-3 substreams:** one stream per program.
- **MP4:** the dac3 and dec3 boxes, compared with the frames.

## Channel layout

From the audio coding mode and the LFE flag. E-AC-3's dependent substreams add their channels to their program's layout.

## Not yet covered

- E-AC-3 frames that use the adaptive hybrid transform or enhanced coupling are read only up to that point.
- Dolby Atmos.

## Encoder detection

No encoder is named from the frames, since no fixture confirms anything that identifies one. Claims from containers and tags name one with low confidence.

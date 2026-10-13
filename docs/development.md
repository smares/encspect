# Development

```sh
dotnet build -c Release
dotnet test -c Release
dotnet run -c Release --project src/Encspect.Cli -- --offsets file.mp3
```

The rules every change follows are in [CLAUDE.md](../CLAUDE.md); the design is in the [plan](PLAN.md).

## Tests

- `tests/Encspect.Tests`: the core, host, parsers and CLI, on xUnit 2.
- `tests/Encspect.Gui.Tests`: the GUI, on Avalonia's headless platform without a display. These run on xUnit v3, which the headless test support needs. Set `ENCSPECT_SCREENSHOTS` to a folder to have them save screenshots of both windows and of the graphs, in light and dark mode.
- **Golden reports** (`*.report.json`) are compared on every test run. After an intended output change, rerun the tests with `ENCSPECT_UPDATE_GOLDEN=1` and review the diff.
- **Fuzzing:** `tests/Encspect.Fuzz` mutates the fixtures. Inputs it once failed on are kept in `tests/fixtures/fuzz`.

## Fixtures and cross-checks

Every format has three kinds of fixture:

- **Hand-built fixtures**, written by a script that CI reruns to check they are reproducible.
- **Encoder-written fixtures**, written by real encoders. `--manifests-only` recomputes their manifests from the committed files without the encoders.
- **A cross-check script**, which confirms the manifests with other tools in its own CI job.

| Format | Fixtures | Scripts | Checked against |
|---|---|---|---|
| Tags | `tests/fixtures/tags` | `make_tag_fixtures.py`; `make_external_tag_fixtures.py` (mutagen, ffmpeg) | TagLib# (`TagLibCrossCheckTests`); MediaInfo where it finds the audio (`crosscheck_mpeg_fixtures.py`) |
| MPEG audio | `tests/fixtures/mpeg`, `layer2`, `layer3`, `vbr`, `stats` | `make_mpeg_fixtures.py`, `make_layer2_fixtures.py`, `make_layer3_fixtures.py`, `make_vbr_fixtures.py`, `make_stats_fixtures.py`; `make_external_mpeg_fixtures.py` | ffprobe, madplay, mpg123, minimp3, mctoolamed, mp3guessenc, EncSpot, MediaInfo (`crosscheck_mpeg_fixtures.py`) |
| MP4 and AAC | `tests/fixtures/mp4`, `adts`, `aac` | `make_mp4_fixtures.py`, `aac_block_fixtures.py`; `make_external_aac_fixtures.py` | ffprobe, MP4Box, faad, a tracing faad2 build, ffmpeg decoding, MediaInfo (`crosscheck_mp4_fixtures.py`); `check_aac_tables.py` for the Huffman and band tables; TagLib# for iTunes metadata |
| Ogg, Speex, Vorbis, Opus | `tests/fixtures/ogg` | `make_ogg_fixtures.py`, `make_vorbis_opus_fixtures.py`; `make_external_ogg_fixtures.py`, `make_external_vorbis_opus_fixtures.py` | oggz, ogginfo, vorbiscomment, opusinfo, ffprobe, speexdec, oggdec, opusdec, MediaInfo (`crosscheck_ogg_fixtures.py`) |
| Musepack | `tests/fixtures/musepack` | `make_musepack_fixtures.py`; `make_external_musepack_fixtures.py` | mpcdec, ffprobe, libmpcdec per frame, libmpcdec 1.2.6 per SV4 to SV6 frame (and the Portable Musepack decoder, its C++ ancestor), mppdec under Wine, MediaInfo (`crosscheck_musepack_fixtures.py`) |
| AC-3 and E-AC-3 | `tests/fixtures/ac3` | `make_ac3_fixtures.py`; `make_external_ac3_fixtures.py` (the derived-* fixtures need `--analyzer`) | ffprobe, ffmpeg, liba52, ffmpeg decoding with skip fields inserted and a negative control, MediaInfo (`crosscheck_ac3_fixtures.py`) |

All scripts are in `scripts/fixtures`.

### MediaInfo

Every cross-check script also compares each fixture's golden report with MediaInfo 24.01 (MediaInfoLib 24.01, Ubuntu 24.04's `mediainfo` package), through `mediainfo_tool.py`. MediaInfo reads container and stream headers without decoding, so it is one more independent reader of the formats' header fields, frame and sample counts, encoder strings and tags.

- **What is compared:** each script's docstring lists the fields. A listed difference that no longer occurs fails the run, and the summary counts each field as compared, compared and absent on both, or skipped with its reason.
- **MediaInfo's own readings** are asserted as such, each with the evidence, mostly a line of MediaInfoLib's source (github.com/MediaArea/MediaInfoLib, tag v24.01; Ubuntu's only patch touches the build). Among them:
  - AC-3: `mixlevel` read as 8 bits instead of A/52's 5, so `dynrng`, the time codes and Annex D's `dsurexmod` come out 3 bits late in a frame with production information; the script reproduces that reading and compares with it.
  - MPEG audio: the names of the Layer I and II mode extension, the frames it parses of a variable bitrate without a VBR header, no encoder without a LAME tag, an encoder string or an ID3v2 TSSE.
  - Musepack: no format for SV4 to SV6 (one superframe stream taken for MPEG-2.5 Layer II) or after a leading ID3v2 tag, the SV7 encoder version read from byte 24 instead of 27, no channels or encoder for SV8.
  - Ogg: the sample count from the rounded duration, no Speex comments (the comment count is skipped), no floor type for a single floor (the floor count read one short) or a broken setup header.
  - MP4 and AAC: the bitrate mode, which follows frame sizes more than the encoder's declaration (ADTS: VBR where the largest frame is more than 2 % above the smallest, else none; MP4: the esds bitrates, else the sample sizes within 1 %), the mono layout of HE-AAC v2, no duration for ADTS, one frame for ADIF.
- **Fixtures it reads otherwise** are listed per fixture with the evidence, for example:
  - MPEG audio: observed, not found in the source: without a VBR header's byte count, the stream's bytes come out as the file size less the leading tags and twice the trailing ones. On `external-blade-mono`'s 16,128 bytes of frames, MediaInfo gives 16,000 with a 128-byte ID3v1 tag, 16,049 with a 79-byte APE tag with a header and 15,921 with both; a 47-byte APE tag without a header, which `Tag/File__Tags.cpp` sizes 32 bytes larger, gives 16,128, and with ID3v1 16,049, which the rule does not fit.
  - MPEG audio: a lone frame that ends the file taken for a stream; "Xing" read in Layer II.
  - Musepack: a crash (SIGFPE) on a reserved SV8 sample rate code.
  - MP4 and AAC: a `crc_check` read in unprotected ADTS frames of several raw data blocks; parametric stereo read in a channel pair, which FFmpeg and faad2 do not apply there.
- **Tags:** of the 145 tag fixtures, MediaInfo reads the title, artist and album of 5 (written by ffmpeg and mutagen). It reads no tag of the 119 whose audio it does not recognize, and the other 21 hold none of these fields; both are asserted empty.
- **Version:** the assertions and skips describe 24.01, so the scripts stop with a message on another version.

### Derived Musepack SV6 fixtures

No SV6 encoder was found, so `make_musepack_fixtures.py` derives the SV6 fixtures (`derived-*-as-sv6.mpc`) from the SV5 encoder's variable bitrate fixtures, with and without mid/side. They are relabeled SV5 streams, not an encoder's output, and their manifests say so (`derived`).

- The stream version becomes 6, and the frame count drops by one: SV5 decoders skip the last counted frame, which the SV5 encoder leaves cut short, while libmpcdec 1.2.6 decodes it in SV6, so keeping the count would leave a cut frame that libmpcdec 1.2.6 (and its C++ ancestor) reports invalid.
- The 11 bits after the new last frame give its samples: 0 (all 1,152) in one fixture, 300 in the other, so both of the decoder's ways of ending the output are covered. The stream ends in the word that holds them.
- `sv6-padding-word` keeps the first 12 frames, whose 11 bits end on a word boundary, and adds one zero word, as mppenc 1.16 ends such a stream.
- libmpcdec 1.2.6 decodes all counted frames of each, with the samples the manifests give (32,256 and 31,404 for the two derived fixtures). The Portable Musepack decoder, the C++ code libmpcdec 1.2.6 was ported from, prints the same lines; it is the same reading in an earlier form, so it checks the builds, not the syntax.
- mppdec (Buschmann, June 2000) is the only separate decoder at hand. Under Wine, it decodes all counted frames of each; the manifests record the line it printed. It confirms that the last counted frame is decoded, not the 11 bits or the sample count.
- So the SV6 frame syntax rests on libmpcdec 1.2.6's source and these derived fixtures, with no encoder output to confirm it.

### Encoders

These are built or fetched by helper scripts:

- the Helix MP3 Encoder: `helix_tool.py`
- Fraunhofer's MP3 Surround encoder: `fhg_tool.py`
- fdk-aac: `fdk_tool.py`
- exhale: `exhale_tool.py`
- aoTuV releases, plus Rarewares' aoTuV and Lancer builds under Wine: `aotuv_tool.py`
- mppenc 1.16, libmpcdec 1.2.6 and its C++ ancestor, the Portable Musepack decoder (built with one portability patch; its archive is only in the private repository, so it needs the token), plus Rarewares' SV4, SV5 and SV7 encoders and mppdec under Wine: `musepack_tool.py`

### Models

The models that compute the expected values are:

- `stream_stats.py` and `ancillary_scan.py` for MPEG audio
- `mp4_boxes.py` and `aac_model.py` for MP4 and AAC
- `ogg_model.py` and `vorbis_opus_model.py` for Ogg
- `musepack_model.py` for Musepack
- `ac3_model.py` for AC-3

## Conformance streams and downloads

- **MPEG-2 conformance:** `conformance_mpeg_audio.py` checks `tests/conformance/mpeg2-audio.json` against the ISO/IEC 13818-4 audio conformance streams. It downloads them, since they may not be redistributed.
- **mp3PRO:** `check_mp3pro_samples.py` checks `tests/conformance/mp3pro-samples.json` against three mp3PRO files from the FFmpeg sample archive, downloaded for the same reason.
- **xHE-AAC by other encoders:** `check_usac_samples.py` checks `tests/conformance/usac-samples.json` against 13 downloaded files: Fraunhofer's demonstration files, the ISO/IEC 23003-3 conformance streams in FFmpeg's FATE suite and one exhale file from it. The private repository holds copies as their fallback address.
  - Without `--analyzer`, a UsacConfig reader of its own reads each file's elements and configuration extensions from the DecoderSpecificInfo that MP4Box lists.
  - With `--analyzer`, the analyzer must show the same and name the encoder the manifest lists, so the exhale layout rule is held against files it must not match.
- **Vendored tools:** minimp3, mp3guessenc and EncSpot are built from the sources in `third_party` (see its README), so the checks do not depend on a download or a distribution's package version.
- **Pinned downloads:** everything still downloaded is pinned by its SHA-256 and falls back to the Wayback Machine's copy where one exists (`downloads.py`). The weekly `sources` workflow checks every address (`check_sources.py`).

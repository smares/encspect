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
| Tags | `tests/fixtures/tags` | `make_tag_fixtures.py`; `make_external_tag_fixtures.py` (mutagen, ffmpeg) | |
| MPEG audio | `tests/fixtures/mpeg`, `layer2`, `layer3`, `vbr`, `stats` | `make_mpeg_fixtures.py`, `make_layer2_fixtures.py`, `make_layer3_fixtures.py`, `make_vbr_fixtures.py`, `make_stats_fixtures.py`; `make_external_mpeg_fixtures.py` | ffprobe, madplay, mpg123, minimp3, mctoolamed, mp3guessenc, EncSpot (`crosscheck_mpeg_fixtures.py`) |
| MP4 and AAC | `tests/fixtures/mp4`, `adts`, `aac` | `make_mp4_fixtures.py`, `aac_block_fixtures.py`; `make_external_aac_fixtures.py` | ffprobe, MP4Box, faad, a tracing faad2 build, ffmpeg decoding (`crosscheck_mp4_fixtures.py`); `check_aac_tables.py` for the Huffman and band tables; TagLib# for iTunes metadata |
| Ogg, Speex, Vorbis, Opus | `tests/fixtures/ogg` | `make_ogg_fixtures.py`, `make_vorbis_opus_fixtures.py`; `make_external_ogg_fixtures.py`, `make_external_vorbis_opus_fixtures.py` | oggz, ogginfo, vorbiscomment, opusinfo, ffprobe, speexdec, oggdec, opusdec (`crosscheck_ogg_fixtures.py`) |
| Musepack | `tests/fixtures/musepack` | `make_musepack_fixtures.py`; `make_external_musepack_fixtures.py` | mpcdec, ffprobe, libmpcdec per frame, libmpcdec 1.2.6 per SV4 to SV6 frame (and the Portable Musepack decoder, its C++ ancestor), mppdec under Wine (`crosscheck_musepack_fixtures.py`) |
| AC-3 and E-AC-3 | `tests/fixtures/ac3` | `make_ac3_fixtures.py`; `make_external_ac3_fixtures.py` (the derived-* fixtures need `--analyzer`) | ffprobe, ffmpeg, liba52, ffmpeg decoding with skip fields inserted and a negative control (`crosscheck_ac3_fixtures.py`) |

All scripts are in `scripts/fixtures`.

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
- **Vendored tools:** minimp3, mp3guessenc and EncSpot are built from the sources in `third_party` (see its README), so the checks do not depend on a download or a distribution's package version.
- **Pinned downloads:** everything still downloaded is pinned by its SHA-256 and falls back to the Wayback Machine's copy where one exists (`downloads.py`). The weekly `sources` workflow checks every address (`check_sources.py`).

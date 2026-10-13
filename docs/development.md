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
| Musepack | `tests/fixtures/musepack` | `make_musepack_fixtures.py`; `make_external_musepack_fixtures.py` | mpcdec, ffprobe, libmpcdec per frame (`crosscheck_musepack_fixtures.py`) |
| AC-3 and E-AC-3 | `tests/fixtures/ac3` | `make_ac3_fixtures.py`; `make_external_ac3_fixtures.py` (the derived-* fixtures need `--analyzer`) | ffprobe, ffmpeg, liba52, ffmpeg decoding with skip fields inserted and a negative control (`crosscheck_ac3_fixtures.py`) |

All scripts are in `scripts/fixtures`.

### Encoders

These are built or fetched by helper scripts:

- the Helix MP3 Encoder: `helix_tool.py`
- Fraunhofer's MP3 Surround encoder: `fhg_tool.py`
- fdk-aac: `fdk_tool.py`
- exhale: `exhale_tool.py`
- aoTuV releases, plus Rarewares' aoTuV and Lancer builds under Wine: `aotuv_tool.py`
- mppenc 1.16: `musepack_tool.py`

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

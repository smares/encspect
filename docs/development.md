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
| ASF and WMA | `tests/fixtures/asf` | `make_asf_fixtures.py`; `make_external_asf_fixtures.py`; `import_windows_wma_fixtures.py` (Microsoft's encoder, below) | ffprobe, ffmpeg decoding (bit-exact for Lossless), MediaInfo (`crosscheck_asf_fixtures.py`); `wma_tables.py --check` for the tables generated from FFmpeg's source |

All scripts are in `scripts/fixtures`.

### Derived Musepack SV6 fixtures

No SV6 encoder was found, so `make_musepack_fixtures.py` derives the SV6 fixtures (`derived-*-as-sv6.mpc`) from the SV5 encoder's variable bitrate fixtures, with and without mid/side. They are relabeled SV5 streams, not an encoder's output, and their manifests say so (`derived`).

- The stream version becomes 6, and the frame count drops by one: SV5 decoders skip the last counted frame, which the SV5 encoder leaves cut short, while libmpcdec 1.2.6 decodes it in SV6, so keeping the count would leave a cut frame that libmpcdec 1.2.6 (and its C++ ancestor) reports invalid.
- The 11 bits after the new last frame give its samples: 0 (all 1,152) in one fixture, 300 in the other, so both of the decoder's ways of ending the output are covered. The stream ends in the word that holds them.
- `sv6-padding-word` keeps the first 12 frames, whose 11 bits end on a word boundary, and adds one zero word, as mppenc 1.16 ends such a stream.
- libmpcdec 1.2.6 decodes all counted frames of each, with the samples the manifests give (32,256 and 31,404 for the two derived fixtures). The Portable Musepack decoder, the C++ code libmpcdec 1.2.6 was ported from, prints the same lines; it is the same reading in an earlier form, so it checks the builds, not the syntax.
- mppdec (Buschmann, June 2000) is the only separate decoder at hand. Under Wine, it decodes all counted frames of each; the manifests record the line it printed. It confirms that the last counted frame is decoded, not the 11 bits or the sample count.
- So the SV6 frame syntax rests on libmpcdec 1.2.6's source and these derived fixtures, with no encoder output to confirm it.

### WMA fixtures from Microsoft's encoders

Microsoft's encoders run on Windows only, so a workflow writes the fixtures and they are committed once. They are checked by content and are not regenerated byte for byte, since the writers stamp a new file ID and creation date every time.

- `.github/workflows/wma-fixtures.yml` runs `scripts/fixtures/windows/make_windows_wma_fixtures.ps1` on a `windows-2025` runner, started by hand or by a push to the branch `fixtures/wma`. It uploads the artifact `wma-fixtures-windows-2025` for 7 days; nothing is released.
- Two encoders write the files, and each record says which (`encoderPath`):
  - the Windows Media Format SDK's writer (`wmvcore.dll`, driven by `WmfWriter.cs`) writes the variable bitrate files of WMA 9.2 and Professional, Lossless and Voice;
  - Media Foundation's transcoder (`MediaEncodingProfile.CreateWma` with `Audio.Bitrate`) writes the constant bitrate files of WMA 9.2 and Professional. The SDK's writer listed only variable bitrate formats in the first runs because every `byte[]` of the COM declarations was marshaled as a SAFEARRAY, so the settings arrived as the SAFEARRAY header (0x00800001): `_VBRENABLED=0` read as true and `_PASSESUSED` failed. The declarations now use `UnmanagedType.LPArray`, every setting and property is read back, and a difference fails the run. Constant bitrate encodes through the SDK are tried as well (names ending `-sdk`); their headers differ from Media Foundation's.
- The input is the signal of `wma_signals.py`, the same one ffmpeg encodes from. A request can come out as something else (Media Foundation gave stereo for a mono request and capped Professional at 256 kbit/s), so the importer names each file after what its header says, and the provenance keeps the request.
- The run fails when a required encode is missing, when a request got the other bitrate mode, when a Media Foundation file is more than half off its bitrate, or when two encodes wrote the same file. Encodes the SDK may not offer (mono, 22 kHz) are optional and recorded with the reason.
- The artifact holds `wma-out/wma/*.wma`, `pcm/*.wav`, `formats.txt` (every format each codec lists and every attempt to list constant bitrate formats), `encodes.json`, `environment.json`, `log.txt` and, on failure, `problems.txt`.
- `python3 scripts/fixtures/import_windows_wma_fixtures.py <artifact directory>` copies the files to `tests/fixtures/asf/external-windows-<name>.wma`, each with a `.provenance.json` (encoder, path, codec, format, request, what the header states, input, runner, SHA-256). Files over 600 KB are refused unless `--allow-large` is given. `make_external_asf_fixtures.py --manifests-only` then writes their manifests.
- The SDK path sets Title and Author for the tagged file as strings (`WMT_TYPE_STRING`, the UTF-16 bytes with the terminating null, the length in bytes) and reads them back from the written file through the metadata editor; the run fails when they differ. The importer checks them again with ffprobe and skips a file whose tags are not the ones written.
- `crosscheck_asf_fixtures.py` checks them with ffprobe, ffmpeg and MediaInfo; the Lossless files must decode bit for bit to the input signal. MediaInfo's bit rate mode, description of the codec, format profile, title, performer, encoded date and maximum overall bit rate are compared with the analyzer's golden report (MediaInfoLib's `File_Wm_Elements.cpp` reads the same header fields).
- What `WmaFixtureTests` holds the analyzer to: the samples it reports equal the samples ffmpeg decodes for all 32 external files; no finding beyond information; every Professional frame ends where its length prefix says; every ffmpeg file's superframes are padded with 'N'; the writer is named from the header.

### Generated tables

`wma_tables.py` writes `src/Encspect.Formats.Wma/Tables.cs` (from FFmpeg 6.1.1's `wmadata.h`, `wma_freqs.c` and `aactab.c`) and `ProTables.cs` (from `wmaprodata.h`); `--check` fails if either differs from what it writes. `ffmpeg_sources.py` and `asf_specs.py` fetch the pinned sources.

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
- `asf_model.py` for ASF

## Conformance streams and downloads

- **MPEG-2 conformance:** `conformance_mpeg_audio.py` checks `tests/conformance/mpeg2-audio.json` against the ISO/IEC 13818-4 audio conformance streams. It downloads them, since they may not be redistributed.
- **mp3PRO:** `check_mp3pro_samples.py` checks `tests/conformance/mp3pro-samples.json` against three mp3PRO files from the FFmpeg sample archive, downloaded for the same reason.
- **xHE-AAC by other encoders:** `check_usac_samples.py` checks `tests/conformance/usac-samples.json` against 13 downloaded files: Fraunhofer's demonstration files, the ISO/IEC 23003-3 conformance streams in FFmpeg's FATE suite and one exhale file from it. The private repository holds copies as their fallback address.
  - Without `--analyzer`, a UsacConfig reader of its own reads each file's elements and configuration extensions from the DecoderSpecificInfo that MP4Box lists.
  - With `--analyzer`, the analyzer must show the same and name the encoder the manifest lists, so the exhale layout rule is held against files it must not match.
- **Vendored tools:** minimp3, mp3guessenc and EncSpot are built from the sources in `third_party` (see its README), so the checks do not depend on a download or a distribution's package version.
- **Pinned downloads:** everything still downloaded is pinned by its SHA-256 and falls back to the Wayback Machine's copy where one exists (`downloads.py`). The weekly `sources` workflow checks every address (`check_sources.py`).

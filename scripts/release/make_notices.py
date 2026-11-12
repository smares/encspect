#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.md, or with --check fails when the committed file differs.

Every text comes from a file, never from memory:

- the restored NuGet packages (~/.nuget/packages, or --packages): LICENSE and THIRD-PARTY-NOTICES files and nuspec copyright lines;
- the runtime pack the CLI was published with (--runtime-deps names the encspect.deps.json of that publish, which says its version);
- scripts/release/licenses/*.txt: the licensors' own license files for the packages that carry only a license expression,
  fetched by --fetch from the repository commit the package's nuspec names, and committed so a run needs no network;
- scripts/release/licenses/libwavpack.txt: the COPYING of libwavpack 5.6.0, whose code the WavPack decoder is derived from. --fetch extracts it
  from the archive scripts/fixtures/wavpack_tool.py pins by SHA-256, so the text is libwavpack's own and the hash is checked;
- scripts/release/licenses/libspeex.txt: the COPYING of libspeex 1.2.1, whose frame walk the Speex parser follows. --fetch extracts it from the
  archive scripts/fixtures/speex_tool.py pins by SHA-256, so the text is libspeex's own and the hash is checked.
- scripts/release/licenses/libvorbis.txt: the COPYING of libvorbis 1.3.7, whose reading of what the Vorbis I specification leaves open the Vorbis decoder and its entropy pass follow.
  --fetch extracts it from the archive scripts/fixtures/vorbis_tool.py pins by SHA-256, so the text is libvorbis's own and the hash is checked.
- scripts/release/licenses/libopus.txt: the COPYING of libopus 1.5.2, whose code (and that of RFC 6716's reference decoder, Simplified BSD) the Opus decoder and its entropy pass follow.
  --fetch extracts it from the archive scripts/fixtures/opus_tool.py pins by SHA-256, so the text is libopus's own and the hash is checked.
- scripts/release/licenses/libmpcdec.txt and libmpcdec-r495-header.txt: the COPYING of libmpcdec 1.2.6 (identical to that of musepack_src r495,
  which --fetch checks) and the license comment of r495's huffman.c, which carries another copyright line. --fetch extracts them from the two
  archives scripts/fixtures/musepack_tool.py pins by SHA-256.
- scripts/release/licenses/monkeys-audio.txt: the License.txt of the Monkey's Audio SDK 13.44, whose code the Monkey's Audio decoder is derived from.
  --fetch extracts it from the archive scripts/fixtures/monkeys_audio_tool.py pins by SHA-256, so the text is the SDK's own and the hash is checked.
- scripts/release/licenses/alac.txt: the LICENSE (Apache License 2.0) of Apple's ALAC reference implementation (github.com/macosforge/alac), whose code the ALAC parser and decoder
  follow. --fetch reads it from the commit scripts/fixtures/alac_tool.py pins, which git checks against the tree.
- scripts/release/licenses/matplotlib-colormaps.txt: the license comment at the top of colormaps.py of github.com/BIDS/colormap (CC0, with a request for
  credit), whose table of matplotlib's magma colormap is the spectrogram's color ramp. --fetch reads the file and checks its SHA-256.
- scripts/release/licenses/tak-stream.txt: the header of Sdk/Source/takStream.pas of TAK 2.3.3 (its copyright and license: "Do with it, whatever you want", with a request that a
  modified version says it is not an official release), which Encspect's TAK parser follows. --fetch extracts it from the archive scripts/fixtures/tak_tool.py pins by SHA-256.
- scripts/release/licenses/minimp3.txt: the dedication at the top of minimp3.h (CC0 1.0, commit ea99364f, vendored in third_party/minimp3), which the MPEG audio decoder and
  its tables follow. It is a copy of the header's text, so no fetch is needed.
- scripts/release/licenses/libebur128.txt: the COPYING (MIT) of libebur128 v1.2.6, whose derivation of the K-weighting coefficients the loudness measurement follows.
  --fetch reads the file at the tag and checks its SHA-256.

Versions of the packages come from src/Encspect.Gui/obj/project.assets.json (run dotnet restore first).

  python3 scripts/release/make_notices.py --runtime-deps src/Encspect.Cli/bin/Release/net10.0/linux-x64/encspect.deps.json
  python3 scripts/release/make_notices.py --check --runtime-deps ...
  python3 scripts/release/make_notices.py --fetch      # refreshes scripts/release/licenses from GitHub
"""

import argparse
import json
import os
import pathlib
import re
import sys
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
LICENSES = ROOT / "scripts" / "release" / "licenses"
OUTPUT = ROOT / "THIRD-PARTY-NOTICES.md"
LIBWAVPACK_COPYING = LICENSES / "libwavpack.txt"
LIBSPEEX_COPYING = LICENSES / "libspeex.txt"
LIBVORBIS_COPYING = LICENSES / "libvorbis.txt"
LIBOPUS_COPYING = LICENSES / "libopus.txt"
LIBMPCDEC_COPYING = LICENSES / "libmpcdec.txt"
LIBMPCDEC_HEADER = LICENSES / "libmpcdec-r495-header.txt"
MONKEYS_AUDIO_LICENSE = LICENSES / "monkeys-audio.txt"
ALAC_LICENSE = LICENSES / "alac.txt"
TAK_LICENSE = LICENSES / "tak-stream.txt"
MINIMP3_LICENSE = LICENSES / "minimp3.txt"
LIBEBUR128_LICENSE = LICENSES / "libebur128.txt"
LIBEBUR128_URL = "https://raw.githubusercontent.com/jiixyj/libebur128/v1.2.6/COPYING"
LIBEBUR128_SHA256 = "d6b4754bb67bdd08b97d5d11b2d7434997a371585a78fe77007149df3af8d09c"
COLORMAPS_LICENSE = LICENSES / "matplotlib-colormaps.txt"
COLORMAPS_URL = "https://raw.githubusercontent.com/BIDS/colormap/master/colormaps.py"
COLORMAPS_SHA256 = "b7478d10e2ccfdb22128867e137f16f55b687e9c225065e91bcbd6bb9e6c0352"

# Package whose nuspec names the repository commit, the license file in that repository, and the committed copy.
FETCHED = [
    ("avalonia", "Avalonia", "licence.md"),
    ("avalonia-datagrid", "Avalonia.Controls.DataGrid", "licence.md"),
    ("nwayland", "NWayland", "licence.md"),
    ("tmds-dbus", "Tmds.DBus.Protocol", "COPYING"),
    ("microcom", "MicroCom.Runtime", "LICENSE"),
]


def read(path):
    """Text with LF line endings and without a byte order mark, ending in one newline."""
    # open(), not Path.read_text(newline=...), which needs Python 3.13.
    with open(path, encoding="utf-8-sig", newline="") as stream:
        text = stream.read()
    return text.replace("\r\n", "\n").rstrip() + "\n"


def nuspec(packages, package_id, version):
    return read(packages / package_id.lower() / version / f"{package_id.lower()}.nuspec")


def nuspec_field(text, tag):
    match = re.search(rf"<{tag}[^>]*>([^<]*)</{tag}>", text)
    return match.group(1).strip() if match else None


def nuspec_commit(text):
    match = re.search(r'<repository[^>]*url="([^"]+)"[^>]*commit="([0-9a-f]+)"', text)
    return match.groups() if match else None


def versions(assets_path):
    assets = json.loads(pathlib.Path(assets_path).read_text(encoding="utf-8"))
    found = {}
    for key, library in assets["libraries"].items():
        if library["type"] == "package":
            name, version = key.split("/")
            found[name] = version
    return found


# The packages the notices cover, and the build-time ones that ship in no build. A restored package in neither list
# fails the run, so a new dependency cannot ship without its notice.
COVERED = {
    "Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent", "Avalonia.Skia", "Avalonia.HarfBuzz", "Avalonia.Win32",
    "Avalonia.X11", "Avalonia.FreeDesktop", "Avalonia.FreeDesktop.AtSpi", "Avalonia.Remote.Protocol", "Avalonia.Wayland",
    "Avalonia.Native", "Avalonia.Controls.DataGrid", "Avalonia.Angle.Windows.Natives", "NWayland", "MicroCom.Runtime",
    "Tmds.DBus.Protocol", "SkiaSharp", "HarfBuzzSharp",
}
COVERED_PREFIXES = ("SkiaSharp.NativeAssets.", "HarfBuzzSharp.NativeAssets.")
# runtime.<rid>.Microsoft.DotNet.ILCompiler holds the NativeAOT runtime libraries linked into the CLI; its notices are
# the runtime pack's, which the .NET runtime row covers. Microsoft.DotNet.ILCompiler is the compiler and ships nothing.
COVERED_SUFFIXES = (".Microsoft.DotNet.ILCompiler",)
BUILD_ONLY = {"Avalonia.BuildServices", "Microsoft.NET.ILLink.Tasks", "Microsoft.DotNet.ILCompiler"}


def uncovered(assets):
    """The packages of the restore graph (every target) that the notices neither cover nor exclude as build-time."""
    found = set()
    for target in assets["targets"].values():
        for key, entry in target.items():
            if entry.get("type") == "package":
                found.add(key.split("/")[0])
    return sorted(
        p for p in found
        if p not in COVERED and not p.startswith(COVERED_PREFIXES) and not p.endswith(COVERED_SUFFIXES) and p not in BUILD_ONLY
    )


def self_test():
    graph = {"targets": {"net10.0": {"Avalonia/1.0": {"type": "package"}, "SkiaSharp.NativeAssets.Linux/1.0": {"type": "package"},
                                     "Avalonia.BuildServices/1.0": {"type": "package"}, "Encspect.Host/1.0": {"type": "project"}}}}
    assert uncovered(graph) == [], uncovered(graph)
    graph["targets"]["net10.0/linux-x64"] = {"Some.NewLibrary/2.0": {"type": "package"}, "Avalonia.Skia/1.0": {"type": "package"},
                                             "Microsoft.DotNet.ILCompiler/1.0": {"type": "package"},
                                             "runtime.linux-x64.Microsoft.DotNet.ILCompiler/1.0": {"type": "package"}}
    assert uncovered(graph) == ["Some.NewLibrary"], uncovered(graph)
    print("make_notices self-test passed.")


def coverage_problems(assets_paths):
    problems = []
    for path in assets_paths:
        if pathlib.Path(path).exists():
            assets = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
            problems += [f"{pathlib.Path(path).parent.parent.name}: {name}" for name in uncovered(assets)]
    return problems


def fetch_libwavpack():
    """COPYING of the pinned libwavpack archive; wavpack_tool.fetch checks the archive's SHA-256."""
    import tarfile
    import tempfile
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import wavpack_tool
    with tempfile.TemporaryDirectory(prefix="libwavpack-") as directory:
        archive = wavpack_tool.fetch(directory)
        with tarfile.open(archive) as tar:
            LIBWAVPACK_COPYING.write_bytes(tar.extractfile("wavpack-5.6.0/COPYING").read())
    print(f"libwavpack: wavpack-5.6.0/COPYING of {wavpack_tool.WAVPACK[0]} (SHA-256 {wavpack_tool.WAVPACK[1]})")


def fetch_libspeex():
    """COPYING of the pinned libspeex archive; speex_tool.fetch checks the archive's SHA-256."""
    import tarfile
    import tempfile
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import speex_tool
    with tempfile.TemporaryDirectory(prefix="libspeex-") as directory:
        with tarfile.open(speex_tool.fetch(directory)) as tar:
            LIBSPEEX_COPYING.write_bytes(tar.extractfile("speex-1.2.1/COPYING").read())
    print(f"libspeex: speex-1.2.1/COPYING of {speex_tool.NAME} (SHA-256 {speex_tool.SHA256})")


def fetch_libvorbis():
    """COPYING of the pinned libvorbis archive; vorbis_tool.fetch checks the archive's SHA-256."""
    import tarfile
    import tempfile
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import vorbis_tool
    with tempfile.TemporaryDirectory(prefix="libvorbis-") as directory:
        with tarfile.open(vorbis_tool.fetch(directory)) as tar:
            LIBVORBIS_COPYING.write_bytes(tar.extractfile("libvorbis-1.3.7/COPYING").read())
    print(f"libvorbis: libvorbis-1.3.7/COPYING of {vorbis_tool.NAME} (SHA-256 {vorbis_tool.SHA256})")


def fetch_libopus():
    """COPYING of the pinned libopus archive; opus_tool.fetch_libopus checks the archive's SHA-256."""
    import tarfile
    import tempfile
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import opus_tool
    with tempfile.TemporaryDirectory(prefix="libopus-") as directory:
        with tarfile.open(opus_tool.fetch_libopus(directory)) as tar:
            LIBOPUS_COPYING.write_bytes(tar.extractfile("opus-1.5.2/COPYING").read())
    print(f"libopus: opus-1.5.2/COPYING of {opus_tool.LIBOPUS_NAME} (SHA-256 {opus_tool.LIBOPUS_SHA256})")


def fetch_libmpcdec():
    """COPYING of libmpcdec 1.2.6 and the license comment of r495's huffman.c; musepack_tool.fetch checks both archives' SHA-256."""
    import tarfile
    import tempfile
    import textwrap
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import musepack_tool
    with tempfile.TemporaryDirectory(prefix="libmpcdec-") as directory:
        old = tarfile.open(musepack_tool.fetch(musepack_tool.LIBMPCDEC_126, pathlib.Path(directory)))
        new = tarfile.open(musepack_tool.fetch(musepack_tool.LIBMPC, pathlib.Path(directory)))
        copying = old.extractfile("libmpcdec-1.2.6/COPYING").read()
        if new.extractfile("musepack_src_r495/libmpcdec/COPYING").read() != copying:
            raise SystemExit("The COPYING of libmpcdec r495 differs from that of 1.2.6; the notices need both.")
        LIBMPCDEC_COPYING.write_bytes(copying)
        source = new.extractfile("musepack_src_r495/libmpcdec/huffman.c").read().decode("utf-8", "replace")
        comment = source[source.index("/*") + 2:source.index("*/")]
        LIBMPCDEC_HEADER.write_text(textwrap.dedent(comment).strip("\n") + "\n", encoding="utf-8", newline="\n")
    print(f"libmpcdec: COPYING of {musepack_tool.LIBMPCDEC_126[0]} and the license comment of huffman.c in {musepack_tool.LIBMPC[0]}")


def fetch_monkeys_audio():
    """License.txt of the pinned Monkey's Audio SDK; monkeys_audio_tool.fetch checks the archive's SHA-256."""
    import tempfile
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import monkeys_audio_tool
    with tempfile.TemporaryDirectory(prefix="monkeys-audio-") as directory:
        MONKEYS_AUDIO_LICENSE.write_text(monkeys_audio_tool.license_text(directory), encoding="utf-8", newline="\n")
    print(f"Monkey's Audio: License.txt of {monkeys_audio_tool.NAME} (SHA-256 {monkeys_audio_tool.SHA256})")


def fetch_tak():
    """The header of takStream.pas in the pinned TAK archive (its copyright and license), which tak_tool.fetch checks by SHA-256."""
    import tempfile
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import tak_tool
    with tempfile.TemporaryDirectory(prefix="tak-") as directory:
        source = (tak_tool.extract(directory) / tak_tool.STREAM_SOURCE).read_bytes().decode("latin-1").replace("\r\n", "\n")
    header = source[source.index("This source module is part of TAK"):source.index("History:")].rstrip() + "\n"
    TAK_LICENSE.write_text(header, encoding="utf-8", newline="\n")
    print(f"TAK: the header of {tak_tool.STREAM_SOURCE} in {tak_tool.NAME} (SHA-256 {tak_tool.SHA256})")


def fetch_alac():
    """LICENSE of Apple's ALAC source at the pinned commit; alac_tool.fetch checks the tree against the commit."""
    import tempfile
    sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
    import alac_tool
    with tempfile.TemporaryDirectory(prefix="alac-") as directory:
        ALAC_LICENSE.write_text(alac_tool.license_text(directory), encoding="utf-8", newline="\n")
    print(f"ALAC: LICENSE of {alac_tool.REPOSITORY} at {alac_tool.COMMIT}")


def fetch_colormaps():
    """The license comment of BIDS/colormap's colormaps.py, which comes before its first line of code; the file's SHA-256 is checked."""
    import hashlib
    with urllib.request.urlopen(COLORMAPS_URL, timeout=60) as response:
        data = response.read()
    if hashlib.sha256(data).hexdigest() != COLORMAPS_SHA256:
        raise SystemExit(f"colormaps.py differs from the file the notices and src/Encspect.Gui/Views/MagmaRamp.cs were made from ({COLORMAPS_SHA256}).")
    source = data.decode("utf-8")
    COLORMAPS_LICENSE.write_text(source.split("__all__")[0].rstrip() + "\n", encoding="utf-8", newline="\n")
    print(f"matplotlib colormaps: the license comment of {COLORMAPS_URL} (SHA-256 {COLORMAPS_SHA256})")


def fetch_libebur128():
    """COPYING of libebur128 v1.2.6 (MIT); the file's SHA-256 is checked."""
    import hashlib
    with urllib.request.urlopen(LIBEBUR128_URL, timeout=60) as response:
        data = response.read()
    if hashlib.sha256(data).hexdigest() != LIBEBUR128_SHA256:
        raise SystemExit(f"The COPYING of libebur128 differs from the file the notices were made from ({LIBEBUR128_SHA256}).")
    LIBEBUR128_LICENSE.write_bytes(data)
    print(f"libebur128: {LIBEBUR128_URL} (SHA-256 {LIBEBUR128_SHA256})")


def fetch(packages, version_of):
    LICENSES.mkdir(parents=True, exist_ok=True)
    fetch_libebur128()
    fetch_colormaps()
    fetch_libwavpack()
    fetch_libspeex()
    fetch_libvorbis()
    fetch_libopus()
    fetch_libmpcdec()
    fetch_monkeys_audio()
    fetch_alac()
    fetch_tak()
    for name, package, source in FETCHED:
        url, commit = nuspec_commit(nuspec(packages, package, version_of[package]))
        base = url.rstrip("/")
        base = base[:-4] if base.endswith(".git") else base
        raw = base.replace("https://github.com/", "https://raw.githubusercontent.com/")
        target = f"{raw}/{commit}/{source}"
        with urllib.request.urlopen(target, timeout=60) as response:
            (LICENSES / f"{name}.txt").write_bytes(response.read())
        print(f"{name}: {target}")


def build(packages, version_of, runtime_deps):
    deps = json.loads(pathlib.Path(runtime_deps).read_text(encoding="utf-8"))
    runtime = next(k for k in deps["libraries"] if k.startswith("runtimepack.Microsoft.NETCore.App.Runtime."))
    pack_name, runtime_version = runtime[len("runtimepack."):].split("/")
    pack = packages / pack_name.lower() / runtime_version

    v = version_of
    skia_license = read(packages / "skiasharp" / v["SkiaSharp"] / "LICENSE.txt")
    skia_notices = read(packages / "skiasharp.nativeassets.linux" / v["SkiaSharp.NativeAssets.Linux"] / "THIRD-PARTY-NOTICES.txt")
    angle = read(packages / "avalonia.angle.windows.natives" / v["Avalonia.Angle.Windows.Natives"] / "LICENSE")
    licenses = {name: read(LICENSES / f"{name}.txt") for name, _, _ in FETCHED}
    libwavpack = read(LIBWAVPACK_COPYING)
    libspeex = read(LIBSPEEX_COPYING)
    libvorbis = read(LIBVORBIS_COPYING)
    libopus = read(LIBOPUS_COPYING)
    libmpcdec = read(LIBMPCDEC_COPYING)
    libmpcdec_header = read(LIBMPCDEC_HEADER)
    monkeys_audio = read(MONKEYS_AUDIO_LICENSE)
    alac = read(ALAC_LICENSE)
    tak = read(TAK_LICENSE)
    colormaps = read(COLORMAPS_LICENSE)
    libebur128 = read(LIBEBUR128_LICENSE)
    minimp3 = read(MINIMP3_LICENSE)
    def copyright_line(package):
        line = nuspec_field(nuspec(packages, package, v[package]), "copyright")
        return f"Package copyright line: {line}\n\n" if line else ""

    avalonia_ids = ["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent", "Avalonia.Skia", "Avalonia.HarfBuzz", "Avalonia.Win32",
                    "Avalonia.X11", "Avalonia.FreeDesktop", "Avalonia.FreeDesktop.AtSpi", "Avalonia.Remote.Protocol"]
    out = []
    a = out.append
    a(f"""# Third-party notices

Encspect is released under the [MIT License](LICENSE). The published builds, the command line tool `encspect` (a native executable, compiled ahead of time) and the GUI `encspect-gui` (a self-contained single file), carry the components below. This file is shipped with each build and shown by the GUI's About window.

Each license text below is copied from a file, never written by hand (the attribution of the Matroska element registry, which is licensed by reference, is the one short text written here; the header of TAK's takStream.pas is a file's text too); `scripts/release/make_notices.py` regenerates this file and the release workflow fails when it differs:

- **Licensors' own license files:** for the packages that carry only a license expression (`MIT`), the license file of the licensor's repository at the commit the package's nuspec names: Avalonia and Avalonia.Controls.DataGrid (`licence.md`), NWayland (`licence.md`), Tmds.DBus (`COPYING`) and MicroCom (`LICENSE`). The copyright line of the package's nuspec is given as well where the package has one.
- **libwavpack:** the `COPYING` of libwavpack 5.6.0, extracted from `wavpack-5.6.0.tar.bz2`, the archive `scripts/fixtures/wavpack_tool.py` pins by SHA-256 (`8cbfa15927d29bcf953db35c0cfca7424344ff43ebe4083daf161577fb839cc1`); `scripts/release/make_notices.py --fetch` checks the hash and refreshes the committed copy `scripts/release/licenses/libwavpack.txt`.
- **libspeex:** the `COPYING` of libspeex 1.2.1, extracted from `speex-1.2.1.tar.gz`, the archive `scripts/fixtures/speex_tool.py` pins by SHA-256 (`4b44d4f2b38a370a2d98a78329fefc56a0cf93d1c1be70029217baae6628feea`); `make_notices.py --fetch` checks the hash and refreshes the committed copy `scripts/release/licenses/libspeex.txt`.
- **libvorbis:** the `COPYING` of libvorbis 1.3.7, extracted from `libvorbis-1.3.7.tar.xz`, the archive `scripts/fixtures/vorbis_tool.py` pins by SHA-256 (`b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b`); `make_notices.py --fetch` checks the hash and refreshes the committed copy `scripts/release/licenses/libvorbis.txt`.
- **libopus:** the `COPYING` of libopus 1.5.2, extracted from `opus-1.5.2.tar.gz`, the archive `scripts/fixtures/opus_tool.py` pins by SHA-256 (`65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1`); `make_notices.py --fetch` checks the hash and refreshes the committed copy `scripts/release/licenses/libopus.txt`.
- **libmpcdec:** the `COPYING` of libmpcdec 1.2.6 (`libmpcdec-1.2.6.tar.bz2`), which is the same as that of `musepack_src` r495, and the license comment at the top of r495's `huffman.c`, which gives the copyright as 2005-2009; both archives are pinned by SHA-256 in `scripts/fixtures/musepack_tool.py` and `make_notices.py --fetch` checks them and refreshes `scripts/release/licenses/libmpcdec.txt` and `libmpcdec-r495-header.txt`.
- **Monkey's Audio SDK:** the `License.txt` of Monkey's Audio SDK 13.44, extracted from `MAC_1344_SDK.zip`, the archive `scripts/fixtures/monkeys_audio_tool.py` pins by SHA-256 (`0c5c16432552f30ba715de44358b43accdf99b1a81546cd467cd32410f9d954e`); `make_notices.py --fetch` checks the hash and refreshes the committed copy `scripts/release/licenses/monkeys-audio.txt`.
- **Apple's ALAC reference implementation:** the `LICENSE` (Apache License 2.0) of github.com/macosforge/alac at the commit `scripts/fixtures/alac_tool.py` pins (`c38887c5c5e64a4b31108733bd79ca9b2496d987`), which git checks against the tree; `make_notices.py --fetch` refreshes the committed copy `scripts/release/licenses/alac.txt`.
- **TAK's takStream.pas:** the header of `Sdk/Source/takStream.pas` of TAK 2.3.3 (its copyright and its license, "Do with it, whatever you want", with the request that a modified version says it is not an official release), extracted from `TAK_2.3.3.zip`, the archive `scripts/fixtures/tak_tool.py` pins by SHA-256 (`40d26992266f1377d576b88b84b88d2ab256dc321e47f90314402a80692aba6a`); `make_notices.py --fetch` checks the hash and refreshes `scripts/release/licenses/tak-stream.txt`. The module itself is not committed.
- **libebur128:** the `COPYING` (MIT) of libebur128 v1.2.6, pinned by its SHA-256 (`d6b4754bb67bdd08b97d5d11b2d7434997a371585a78fe77007149df3af8d09c`); `make_notices.py --fetch` checks the hash and refreshes `scripts/release/licenses/libebur128.txt`.
- **minimp3:** the dedication at the top of `minimp3.h` (CC0 1.0), copied into `scripts/release/licenses/minimp3.txt`; the source is vendored in `third_party/minimp3` at commit `ea99364f`.
- **matplotlib colormaps:** the license comment at the top of `colormaps.py` of BIDS/colormap, the file the GUI's color ramp for the spectrogram (the 256 entries of `magma`, `src/Encspect.Gui/Views/MagmaRamp.cs`) is taken from, pinned by its SHA-256 (`b7478d10e2ccfdb22128867e137f16f55b687e9c225065e91bcbd6bb9e6c0352`); `make_notices.py --fetch` checks the hash and refreshes `scripts/release/licenses/matplotlib-colormaps.txt`.
- **Package files:** the `LICENSE` and `THIRD-PARTY-NOTICES` files in the restored packages of ANGLE, SkiaSharp and HarfBuzzSharp.
- **.NET runtime:** `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` of the `Microsoft.NETCore.App.Runtime` package, version {runtime_version}.

## Components

| Component | Version | License | In |
|---|---|---|---|
| .NET runtime and base class libraries | {runtime_version} | MIT, with the notices of its own third-party code | both builds, all platforms |
| Avalonia ({", ".join(avalonia_ids)}) | {v["Avalonia"]} | MIT | GUI |
| Avalonia.Wayland | {v["Avalonia.Wayland"]} | MIT | GUI |
| Avalonia.Native: the managed `Avalonia.Native.dll` | {v["Avalonia.Native"]} | MIT | GUI, all platforms |
| Avalonia.Native: the native library `libAvaloniaNative.dylib` | {v["Avalonia.Native"]} | MIT | GUI on macOS only |
| Avalonia.Controls.DataGrid | {v["Avalonia.Controls.DataGrid"]} | MIT | GUI |
| NWayland | {v["NWayland"]} | MIT | GUI |
| MicroCom.Runtime | {v["MicroCom.Runtime"]} | MIT | GUI |
| Tmds.DBus.Protocol | {v["Tmds.DBus.Protocol"]} | MIT | GUI |
| ANGLE, as Avalonia.Angle.Windows.Natives (`av_libglesv2.dll`) | {v["Avalonia.Angle.Windows.Natives"]} | BSD 3-Clause | GUI on Windows |
| SkiaSharp, with the Skia native library | {v["SkiaSharp"]} | MIT (SkiaSharp); Skia is BSD 3-Clause, see the notices of SkiaSharp below | GUI |
| HarfBuzzSharp, with the HarfBuzz native library | {v["HarfBuzzSharp"]} | MIT (HarfBuzzSharp); HarfBuzz is "Old MIT", see the notices of HarfBuzzSharp below | GUI |
| libwavpack 5.6.0: the code of Encspect's WavPack decoder derived from it (the entropy decoder, the decorrelation passes, the float restore, the DSD decoders, the sub-block reading) | 5.6.0 | BSD 3-Clause | both builds, all platforms |
| Monkey's Audio SDK 13.44: the code of Encspect's Monkey's Audio parser and decoder derived from it (the header and seek table layouts, the order of the bytes of a frame, the range decoders and Rice codes, the predictors of every version, the neural network filters, the frame checksum and the output of the samples) | 13.44 | BSD 3-Clause | both builds, all platforms |
| Apple's ALAC reference implementation (commit c38887c5): the code of Encspect's ALAC parser and decoder derived from it (the magic cookie layout, the syntax of a frame's elements and the adaptive Golomb code of the residuals, which the walk of a frame follows; the adaptive predictor, the undoing of the channel mixing and the placing of the shifted bytes, which the decoder follows) | c38887c5 | Apache License 2.0 | both builds, all platforms |
| libspeex 1.2.1: the code of Encspect's Speex parser derived from it (the walk over a packet's frames: the narrowband decoder's reading of the submodes, the skip of wideband layers, the in-band handling) and the tables of bits per submode | 1.2.1 | BSD 3-Clause | both builds, all platforms |
| libvorbis 1.3.7: the code of Encspect's Vorbis decoder and of its entropy pass derived from it (the assignment and decoding of codebook codewords, the reading of the floors and residues and their end-of-packet behavior, the floor 1 and residue reconstruction, the overlap of the blocks) and the table of floor 1 amplitudes (format data, also in the specification) | 1.3.7 | BSD 3-Clause | both builds, all platforms |
| libopus 1.5.2 and the reference decoder of RFC 6716: the code of Encspect's Opus decoder and of its entropy pass derived from them (the range decoder, the CELT and SILK frame syntax and reconstruction, the redundant frames and mode transitions) and the numeric tables (format data) | 1.5.2 | BSD 3-Clause | both builds, all platforms |
| libmpcdec 1.2.6 and r495: the Huffman and resolution tables of the Musepack parser, taken from its sources, and its reading of the frame headers of stream versions 4 to 7 | 1.2.6, r495 | BSD 3-Clause | both builds, all platforms |
| libebur128 v1.2.6: the derivation of the K-weighting filter's coefficients for sample rates other than 48 kHz in Encspect's loudness measurement (the filters' parameters and the bilinear transforms) | v1.2.6 | MIT | both builds, all platforms |
| minimp3 (commit ea99364f): the Layer III Huffman tables, scalefactor band tables and polyphase window of Encspect's MPEG audio decoder (format data), and the decoder's stages and the entropy pass, which follow its code operation by operation | ea99364f | CC0 1.0 (public domain dedication) | both builds, all platforms |
| TAK's `takStream.pas` (TAK 2.3.3): the reading of the container in Encspect's TAK parser (the metadata objects and their layout, the stream info's bit layout, the CRC-24, the frame header and the frame size types). Encspect's reader is not an official release of TAK | 2.3.3 (module version 2.2.0) | "Do with it, whatever you want", with conditions | both builds, all platforms |
| The Matroska element registry (`ebml_matroska.xml`): the default values of the elements and the descriptions of the elements RFC 9559 does not list, in `src/Encspect.Formats.Matroska/Elements.cs` (format data) | commit 1a943ead9bb4 | CC BY 4.0 | both builds, all platforms |
| matplotlib's "magma" colormap, from BIDS/colormap: the 256 colors of the GUI's spectrogram ramp (format data) | colormaps.py, SHA-256 b7478d10 | CC0 1.0 (public domain dedication), with a request for credit | GUI |

The command line tool contains the NativeAOT build of the .NET runtime and the library code of the runtime pack, and Encspect's own code. The packages it is compiled and linked from (`Microsoft.NETCore.App.Runtime.NativeAOT` and `runtime.<rid>.Microsoft.DotNet.ILCompiler`, which holds the native runtime libraries) carry a `THIRD-PARTY-NOTICES.TXT` byte-identical to the runtime pack's, so the .NET runtime row above covers them. On Linux it also links a few objects of the Ubuntu 18.04 sysroot it is built against (found in the binary's symbols and its `.comment` section): glibc's start files `crt1.o`, `crti.o` and `crtn.o` and the `__libc_csu_init`, `__libc_csu_fini` and `atexit` pieces of `libc_nonshared.a`, under glibc's license (LGPL 2.1 or later) with the linking exception glibc gives these start-up objects, which does not make the program a work covered by the LGPL; and GCC 7's `crtbegin.o` and `crtend.o`, under GPL 3 or later with the GCC Runtime Library Exception 3.1. None of `libgcc.a` is linked in. The program loads the system's C library at run time.

The sources in `third_party/` (minimp3, getID3, EncSpot, mp3guessenc) are test tools for the cross-checks. No project references them, and they are in neither build; the MPEG audio decoder follows minimp3's code (below).

## MIT License: Avalonia, Avalonia.Native, Avalonia.Wayland and the other Avalonia packages

{copyright_line("Avalonia")}The license file of AvaloniaUI/Avalonia:

```
{licenses["avalonia"]}```

## MIT License: Avalonia.Controls.DataGrid

{copyright_line("Avalonia.Controls.DataGrid")}The license file of AvaloniaUI/Avalonia.Controls.DataGrid:

```
{licenses["avalonia-datagrid"]}```

## MIT License: NWayland

{copyright_line("NWayland")}The license file of kekekeks/NWayland:

```
{licenses["nwayland"]}```

## MIT License: MicroCom.Runtime

{copyright_line("MicroCom.Runtime")}The license file of kekekeks/MicroCom:

```
{licenses["microcom"]}```

## MIT License: Tmds.DBus.Protocol

{copyright_line("Tmds.DBus.Protocol")}The `COPYING` of tmds/Tmds.DBus:

```
{licenses["tmds-dbus"]}```

## ANGLE (Avalonia.Angle.Windows.Natives)

The license file of the package:

```
{angle}```

## SkiaSharp and HarfBuzzSharp

The `LICENSE.txt` of both packages:

```
{skia_license}```

The packages carry the same `THIRD-PARTY-NOTICES.txt` for the Skia and HarfBuzz native libraries, copied here unchanged. It covers ANGLE, HarfBuzz, Skia and the libraries Skia is built with.

```
{skia_notices}```

## libwavpack

Encspect's WavPack decoder is written in C#; the parts derived from libwavpack 5.6.0 (`src/Encspect.Formats.WavPack`) say so in their source files. The `COPYING` of libwavpack:

```
{libwavpack}```

## Monkey's Audio SDK

Encspect's Monkey's Audio decoder is written in C#; the parts derived from the Monkey's Audio SDK 13.44 (`src/Encspect.Formats.MonkeysAudio`) say so in their source files. The `License.txt` of the SDK:

```
{monkeys_audio}```

## Apple's ALAC reference implementation

Encspect's ALAC parser and decoder are written in C#; the parts derived from Apple's ALAC reference implementation (github.com/macosforge/alac, commit c38887c5c5e64a4b31108733bd79ca9b2496d987; `src/Encspect.Formats.Alac`) say so in their source files. The source files of the implementation carry the Copyright (c) 2011 Apple Inc. notice and the Apache License header; the file `codec/APPLE_LICENSE.txt` in the repository (the Apple Public Source License) is a leftover that no source file refers to. The source files Encspect follows carry the Apache header; the two text files it cites (`ALACMagicCookieDescription.txt` and `ReadMe.txt`) carry none, and the `LICENSE` of the repository covers them:

```
{alac}```

## libspeex

Encspect's Speex parser is written in C#; the walk over a packet's frames (`src/Encspect.Formats.Speex/SpeexFrames.cs`) follows libspeex 1.2.1 (`nb_decode` and `wb_skip_table` of `nb_celp.c`, `sb_decode` of `sb_celp.c`, `speex_inband_handler` of `speex_callbacks.c`); its tables of bits per submode are those of `modes.c` (narrowband) and `modes_wb.c` (wideband and ultra-wideband), and its table of in-band request sizes that of `speex_callbacks.c`. The source file says so. The `COPYING` of libspeex, which names Jean-Marc Valin, the Xiph.org Foundation and the other copyright holders:

```
{libspeex}```

## libvorbis

Encspect's Vorbis decoder is written from the Vorbis I specification, and where the specification leaves details open it follows libvorbis 1.3.7 (https://xiph.org/vorbis/, `lib/`): the assignment of codewords and the refusal of codes that are not complete prefix codes (`sharedbook.c`), the decoding of codewords and what a packet's end does to it (`codebook.c`), the reading of the floors and residues and the end of a packet inside them (`floor1.c`, `floor0.c`, `res0.c`, `mapping0.c`), the setup header's refusals (`info.c`), the unquantization of codebook vectors (`sharedbook.c`) and the overlap of blocks (`block.c`). The source files that follow it say so (`PacketBits.cs`, `Codebook.cs`, `SetupModel.cs`, `PacketWalker.cs`, `Decoding/*.cs`). The `COPYING` of libvorbis, which names the Xiph.org Foundation:

```
{libvorbis}```

## libopus

Encspect's Opus decoder and its entropy pass are written from RFC 6716 as RFC 8251 updates it (the reference decoder in its Appendix A is under the Simplified BSD License, with the copyright notice of the RFC), and follow libopus 1.5.2 (https://opus-codec.org/, `celt/`, `silk/`, `src/`) where they follow code closely: the range decoder (`entdec.c`, `entcode.c`), the reading and reconstruction of CELT frames (`celt_decoder.c`, `bands.c`, `quant_bands.c`, `rate.c`, `cwrs.c`, `vq.c`, `modes.c`, `celt.c`, `mdct.c`, `kiss_fft.c`, `pitch.c`, `lpc.c`), the reading and reconstruction of SILK frames (`silk/dec_API.c`, `decode_frame.c`, `decode_indices.c`, `decode_pulses.c`, `decode_core.c`, `decode_parameters.c`, `NLSF_decode.c`, `NLSF2A.c`, `LPC_inv_pred_gain.c`, `bwexpander.c`, `stereo_decode_pred.c`, `stereo_MS_to_LR.c`, `resampler*.c`, `PLC.c`, `CNG.c`) and the frame flow, redundancy and mode transitions of `opus_decoder.c` and `opus_multistream_decoder.c`. The numeric tables (codebooks, band edges, the window, cosine and pulse tables) are format data that libopus is the public source of. The source files that follow it say so (`RangeDecoder.cs`, `CeltSyntax.cs`, `SilkSyntax.cs`, `SilkMath.cs`, `OpusWalker.cs`, `OpusTables.cs`, `Decoding/*.cs`). The `COPYING` of libopus, which names the Xiph.Org Foundation and the other authors:

```
{libopus}```

## libmpcdec

The tables of Encspect's Musepack parser (`src/Encspect.Formats.Musepack`: the Huffman code tables and resolution tables of stream versions 4 to 7, and the reading of their frame headers in `Sv7Parser.cs` and `OldStreamParser.cs`) are taken from or follow libmpcdec 1.2.6 and from `musepack_src` r495, whose files carry two copyright lines. The `COPYING` of libmpcdec 1.2.6 (the file of r495 is the same):

```
{libmpcdec}```

The license comment at the top of the source files of r495:

```
{libmpcdec_header}```

## TAK's takStream.pas

Encspect's TAK parser (`src/Encspect.Formats.Tak`) is written in C#; its reading of the container (the metadata objects, the stream info's bit layout, the CRC-24 of RFC 2440, the frame header and the frame size types) follows `Sdk/Source/takStream.pas` of the TAK 2.3.3 SDK, the author's own source of the container, where the files Takc.exe wrote agree with it and departs from it where they do not (`docs/formats/tak.md`). The parser's source says so. **Encspect's reader is not an official release of TAK**; the module is not distributed with Encspect. The header of the module, which carries its copyright and license:

```
{tak}```

## The Matroska element registry

`src/Encspect.Formats.Matroska/Elements.cs` is generated from RFC 9559 and RFC 8794 and from the Matroska element registry (`ebml_matroska.xml` of github.com/ietf-wg-cellar/matroska-specification, at commit 1a943ead9bb4c67b02b966cebafe51c8c074e703, pinned by SHA-256 in `scripts/fixtures/matroska_specs.py`), a work of the IETF CELLAR working group and the Matroska project. It takes the default value of every element and the descriptions of the elements RFC 9559 does not list from the registry, each description reduced to the first sentence of the registry's definition with its references written as plain text. The registry is licensed under the Creative Commons Attribution 4.0 International license (CC BY 4.0, https://creativecommons.org/licenses/by/4.0/legalcode), as the `LICENSE.md` of that repository says. The file at the pinned commit is https://github.com/ietf-wg-cellar/matroska-specification/blob/1a943ead9bb4c67b02b966cebafe51c8c074e703/ebml_matroska.xml. The changes are the ones named here. The material is provided under the license's disclaimer of warranties and limitation of liability (section 5 of the license, https://creativecommons.org/licenses/by/4.0/legalcode), to which this notice refers: it is offered as is, without representations or warranties of any kind.

## libebur128: the K-weighting filter of the loudness measurement

The derivation of the K-weighting filter's coefficients for sample rates other than 48 kHz in `src/Encspect.Host/Measurement/KWeighting.cs` follows libebur128 v1.2.6 (`ebur128_init_filter`): the parameters of the two filters and the bilinear transforms. The tangent and the powers are Encspect's own deterministic functions. The `COPYING` of libebur128, Copyright (c) 2011 Jan Kokemüller:

```
{libebur128}```

## minimp3: the MPEG audio decoder

The MPEG audio decoder (`src/Encspect.Formats.Mpeg/Decoding`) and the Layer III entropy pass (`Layer3Syntax.cs`, `Layer12Syntax.cs`) follow minimp3 (https://github.com/lieff/minimp3, commit `ea99364f`, vendored in `third_party/minimp3`): the requantization, stereo processing, antialias, inverse transform and polyphase synthesis stages operation by operation in single precision, and the tables (`Layer3Tables.Data.cs`, `Decoding/SynthesisTables.cs`, generated by `scripts/fixtures/mpeg_tables.py`) as format data. Its authors dedicated it to the public domain under CC0 1.0, which asks for nothing; it is named here so that the source is on record.

```
{minimp3}```

## matplotlib colormaps: the spectrogram's color ramp

The 256 colors of the GUI's spectrogram (`src/Encspect.Gui/Views/MagmaRamp.cs`) are the entries of the "magma" colormap that Nathaniel J. Smith and Stefan van der Walt made for matplotlib (Eric Firing joined them for "viridis"), from `colormaps.py` of github.com/BIDS/colormap. The file releases the colormaps under the CC0 license (a public domain dedication) and asks for credit, which is given here. The license comment at the top of the file:

```
{colormaps}```

## .NET runtime

The `LICENSE.TXT` of the runtime package:

```
{read(pack / "LICENSE.TXT")}```

Its `THIRD-PARTY-NOTICES.TXT`, copied here unchanged:

```
{read(pack / "THIRD-PARTY-NOTICES.TXT")}```
""")
    return "".join(out)


def main():
    if hasattr(sys.stdout, "reconfigure"):
        # The diff holds non-ASCII characters from the license files; a Windows console would fail to print them.
        sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--packages", default=os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages"))
    parser.add_argument("--assets", default=str(ROOT / "src" / "Encspect.Gui" / "obj" / "project.assets.json"))
    parser.add_argument("--runtime-deps", help="encspect.deps.json of the CLI publish (it names the runtime pack the NativeAOT build compiled in)")
    parser.add_argument("--check", action="store_true", help="fail when THIRD-PARTY-NOTICES.md differs")
    parser.add_argument("--self-test", action="store_true", help="test the check for packages the notices do not cover")
    parser.add_argument("--fetch", action="store_true", help="download the licensors' license files into scripts/release/licenses")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return 0

    packages = pathlib.Path(args.packages)
    version_of = versions(args.assets)
    missing = coverage_problems([args.assets, ROOT / "src" / "Encspect.Cli" / "obj" / "project.assets.json"])
    if missing:
        print("The restored packages include some THIRD-PARTY-NOTICES.md does not cover; add their license to make_notices.py (or list them as build-time only):")
        print("\n".join("  " + m for m in missing))
        return 1
    if args.fetch:
        fetch(packages, version_of)
        return 0
    if not args.runtime_deps:
        parser.error("--runtime-deps is required")

    text = build(packages, version_of, args.runtime_deps)
    if args.check:
        with open(OUTPUT, encoding="utf-8", newline="") as stream:
            current = stream.read().replace("\r\n", "\n")
        if current != text:
            import difflib
            diff = list(difflib.unified_diff(current.splitlines(), text.splitlines(), "committed", "generated", lineterm="", n=1))
            print("THIRD-PARTY-NOTICES.md is out of date; run scripts/release/make_notices.py and commit the result.")
            print("\n".join(diff[:40]))
            return 1
        print("THIRD-PARTY-NOTICES.md is up to date.")
        return 0
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as stream:
        stream.write(text)
    print(f"Wrote {OUTPUT.relative_to(ROOT)} ({len(text)} characters)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

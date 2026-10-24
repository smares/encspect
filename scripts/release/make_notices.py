#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.md, or with --check fails when the committed file differs.

Every text comes from a file, never from memory:

- the restored NuGet packages (~/.nuget/packages, or --packages): LICENSE and THIRD-PARTY-NOTICES files and nuspec copyright lines;
- the runtime pack the CLI was published with (--runtime-deps names the encspect.deps.json of that publish, which says its version);
- scripts/release/licenses/*.txt: the licensors' own license files for the packages that carry only a license expression,
  fetched by --fetch from the repository commit the package's nuspec names, and committed so a run needs no network;
- scripts/release/licenses/libwavpack.txt: the COPYING of libwavpack 5.6.0, whose code the WavPack decoder is derived from. --fetch extracts it
  from the archive scripts/fixtures/wavpack_tool.py pins by SHA-256, so the text is libwavpack's own and the hash is checked;
- scripts/release/licenses/libmpcdec.txt and libmpcdec-r495-header.txt: the COPYING of libmpcdec 1.2.6 (identical to that of musepack_src r495,
  which --fetch checks) and the license comment of r495's huffman.c, which carries another copyright line. --fetch extracts them from the two
  archives scripts/fixtures/musepack_tool.py pins by SHA-256.

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
LIBMPCDEC_COPYING = LICENSES / "libmpcdec.txt"
LIBMPCDEC_HEADER = LICENSES / "libmpcdec-r495-header.txt"

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
BUILD_ONLY = {"Avalonia.BuildServices", "Microsoft.NET.ILLink.Tasks"}


def uncovered(assets):
    """The packages of the restore graph (every target) that the notices neither cover nor exclude as build-time."""
    found = set()
    for target in assets["targets"].values():
        for key, entry in target.items():
            if entry.get("type") == "package":
                found.add(key.split("/")[0])
    return sorted(p for p in found if p not in COVERED and not p.startswith(COVERED_PREFIXES) and p not in BUILD_ONLY)


def self_test():
    graph = {"targets": {"net10.0": {"Avalonia/1.0": {"type": "package"}, "SkiaSharp.NativeAssets.Linux/1.0": {"type": "package"},
                                     "Avalonia.BuildServices/1.0": {"type": "package"}, "Encspect.Host/1.0": {"type": "project"}}}}
    assert uncovered(graph) == [], uncovered(graph)
    graph["targets"]["net10.0/linux-x64"] = {"Some.NewLibrary/2.0": {"type": "package"}, "Avalonia.Skia/1.0": {"type": "package"}}
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


def fetch(packages, version_of):
    LICENSES.mkdir(parents=True, exist_ok=True)
    fetch_libwavpack()
    fetch_libmpcdec()
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
    libmpcdec = read(LIBMPCDEC_COPYING)
    libmpcdec_header = read(LIBMPCDEC_HEADER)
    def copyright_line(package):
        line = nuspec_field(nuspec(packages, package, v[package]), "copyright")
        return f"Package copyright line: {line}\n\n" if line else ""

    avalonia_ids = ["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent", "Avalonia.Skia", "Avalonia.HarfBuzz", "Avalonia.Win32",
                    "Avalonia.X11", "Avalonia.FreeDesktop", "Avalonia.FreeDesktop.AtSpi", "Avalonia.Remote.Protocol"]
    out = []
    a = out.append
    a(f"""# Third-party notices

Encspect is released under the [MIT License](LICENSE). The published builds, the command line tool `encspect` and the GUI `encspect-gui`, are self-contained single files that carry the components below. This file is shipped with each build and shown by the GUI's About window.

Each text below is copied from a file, never written by hand; `scripts/release/make_notices.py` regenerates this file and the release workflow fails when it differs:

- **Licensors' own license files:** for the packages that carry only a license expression (`MIT`), the license file of the licensor's repository at the commit the package's nuspec names: Avalonia and Avalonia.Controls.DataGrid (`licence.md`), NWayland (`licence.md`), Tmds.DBus (`COPYING`) and MicroCom (`LICENSE`). The copyright line of the package's nuspec is given as well where the package has one.
- **libwavpack:** the `COPYING` of libwavpack 5.6.0, extracted from `wavpack-5.6.0.tar.bz2`, the archive `scripts/fixtures/wavpack_tool.py` pins by SHA-256 (`8cbfa15927d29bcf953db35c0cfca7424344ff43ebe4083daf161577fb839cc1`); `scripts/release/make_notices.py --fetch` checks the hash and refreshes the committed copy `scripts/release/licenses/libwavpack.txt`.
- **libmpcdec:** the `COPYING` of libmpcdec 1.2.6 (`libmpcdec-1.2.6.tar.bz2`), which is the same as that of `musepack_src` r495, and the license comment at the top of r495's `huffman.c`, which gives the copyright as 2005-2009; both archives are pinned by SHA-256 in `scripts/fixtures/musepack_tool.py` and `make_notices.py --fetch` checks them and refreshes `scripts/release/licenses/libmpcdec.txt` and `libmpcdec-r495-header.txt`.
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
| libmpcdec 1.2.6 and r495: the Huffman and resolution tables of the Musepack parser, taken from its sources | 1.2.6, r495 | BSD 3-Clause | both builds, all platforms |

The command line tool contains the .NET runtime and Encspect's own code only.

The sources in `third_party/` (minimp3, getID3, EncSpot, mp3guessenc) are test tools for the cross-checks. No project references them, and they are in neither build.

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

## libmpcdec

The tables of Encspect's Musepack parser (`src/Encspect.Formats.Musepack`: the Huffman code tables and resolution tables of stream versions 4 to 7) are taken from libmpcdec 1.2.6 and from `musepack_src` r495, whose files carry two copyright lines. The `COPYING` of libmpcdec 1.2.6 (the file of r495 is the same):

```
{libmpcdec}```

The license comment at the top of the source files of r495:

```
{libmpcdec_header}```

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
    parser.add_argument("--runtime-deps", help="encspect.deps.json of the self-contained CLI build")
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

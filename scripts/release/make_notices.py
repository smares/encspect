#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.md, or with --check fails when the committed file differs.

Every text comes from a file, never from memory:

- the restored NuGet packages (~/.nuget/packages, or --packages): LICENSE and THIRD-PARTY-NOTICES files and nuspec copyright lines;
- the runtime pack the CLI was published with (--runtime-deps names the encspect.deps.json of that publish, which says its version);
- scripts/release/licenses/*.txt: the licensors' own license files for the packages that carry only a license expression,
  fetched by --fetch from the repository commit the package's nuspec names, and committed so a run needs no network.

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


def fetch(packages, version_of):
    LICENSES.mkdir(parents=True, exist_ok=True)
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
    avalonia_copyright = nuspec_field(nuspec(packages, "Avalonia", v["Avalonia"]), "copyright")

    avalonia_ids = ["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent", "Avalonia.Skia", "Avalonia.HarfBuzz", "Avalonia.Win32",
                    "Avalonia.X11", "Avalonia.FreeDesktop", "Avalonia.FreeDesktop.AtSpi", "Avalonia.Remote.Protocol"]
    out = []
    a = out.append
    a(f"""# Third-party notices

Encspect is released under the [MIT License](LICENSE). The published builds, the command line tool `encspect` and the GUI `encspect-gui`, are self-contained single files that carry the components below. This file is shipped with each build and shown by the GUI's About window.

Each text below is copied from a file, never written by hand; `scripts/release/make_notices.py` regenerates this file and the release workflow fails when it differs:

- **Licensors' own license files:** for the packages that carry only a license expression (`MIT`), the license file of the licensor's repository at the commit the package's nuspec names: Avalonia and Avalonia.Controls.DataGrid (`licence.md`), NWayland (`licence.md`), Tmds.DBus (`COPYING`) and MicroCom (`LICENSE`). The copyright line of the package's nuspec is given as well where it differs.
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

The command line tool contains the .NET runtime and Encspect's own code only.

The sources in `third_party/` (minimp3, getID3, EncSpot, mp3guessenc) are test tools for the cross-checks. No project references them, and they are in neither build.

## MIT License: Avalonia, Avalonia.Native, Avalonia.Wayland and the other Avalonia packages

Package copyright line: {avalonia_copyright}

The license file of AvaloniaUI/Avalonia:

```
{licenses["avalonia"]}```

## MIT License: Avalonia.Controls.DataGrid

The license file of AvaloniaUI/Avalonia.Controls.DataGrid:

```
{licenses["avalonia-datagrid"]}```

## MIT License: NWayland

The license file of kekekeks/NWayland:

```
{licenses["nwayland"]}```

## MIT License: MicroCom.Runtime

The license file of kekekeks/MicroCom:

```
{licenses["microcom"]}```

## MIT License: Tmds.DBus.Protocol

The `COPYING` of tmds/Tmds.DBus:

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
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--packages", default=os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages"))
    parser.add_argument("--assets", default=str(ROOT / "src" / "Encspect.Gui" / "obj" / "project.assets.json"))
    parser.add_argument("--runtime-deps", help="encspect.deps.json of the self-contained CLI build")
    parser.add_argument("--check", action="store_true", help="fail when THIRD-PARTY-NOTICES.md differs")
    parser.add_argument("--fetch", action="store_true", help="download the licensors' license files into scripts/release/licenses")
    args = parser.parse_args()

    packages = pathlib.Path(args.packages)
    version_of = versions(args.assets)
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

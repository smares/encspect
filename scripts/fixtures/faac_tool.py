"""Builds FAAC, the Freeware Advanced Audio Coder, for encoder-written fixtures: the releases 1.31.1, 1.40
and 1.50 (which write "libfaac <version>" into a fill element) and 2.0, 2.1 and 2.2 (which do not, and add
HE-AAC), each from its tag's commit in github.com/FreewareAdvancedAudio/faac (before it,
github.com/knik0/faac, which now redirects there; both serve the same commits).

The commit is the pin, as for fdk_tool.py and exhale_tool.py: git checks the whole tree against it, where a
tag archive's bytes (GitHub builds it on request) are not promised to stay the same. check_sources.py asks
both repositories whether each tag still names its commit.

- 1.31.1 builds with autotools (bootstrap, configure, make), 1.40 and later with meson (and ninja); the
  libraries are static, so the front end runs from wherever it is copied to.
- A 2.x front end writes the MP4 tool atom "FAAC <version> (<commit>)" with the abbreviated commit of
  `git describe --always --dirty`; the build pins its length to 7 (core.abbrev), which the repository
  would otherwise choose by its size. A build from a tarball, without .git, writes "FAAC <version>".
- FAAC refuses to overwrite its output, and 1.x writes the current time into the MP4 movie header: regenerating
  an M4A fixture changes its bytes (the 2.x files are the same each time).
- 1.40 and 1.50 name their versions "1.40.0" and "1.50.0" (meson.build's project version), and write them
  into the stream as "libfaac 1.40.0" and "libfaac 1.50.0"; 1.31.1 writes "libfaac 1.31.1".

FAAC is under the GNU Lesser General Public License, version 2.1 or later, with the ISO MPEG-4 reference code's
license for the code it is based on; it is built for encoding this project's own test signals and not
redistributed. Needs git, gcc, autoconf, automake and libtool for 1.31.1, meson and ninja (pip install meson
ninja) for the others.
"""

import os
import pathlib
import shutil
import subprocess
import tempfile

REPOSITORIES = ["https://github.com/FreewareAdvancedAudio/faac.git", "https://github.com/knik0/faac.git"]

# version: (tag, commit, build system, what the front end prints first, then the version)
RELEASES = {
    "1.31.1": ("faac-1.31.1", "3aec0c1cef9442fe2f8eb86a5b475c68e94f3306", "autotools", "FAAC 1.31.1"),
    "1.40": ("faac-1.40", "c899ee44ac1a0ca96c816e6c4d3de5a722d1cf56", "meson", "FAAC 1.40.0"),
    "1.50": ("faac-1.50", "79329efee51c9d3545bc4c7179b43a23fe350b6b", "meson", "FAAC 1.50.0"),
    "2.0": ("faac-2.0", "19504462aef3d444aa09b9cfcad0e2146889fb3b", "meson", "FAAC 2.0.0"),
    "2.1": ("faac-2.1", "028707109fe62da278870138e152ac664f9c6a4f", "meson", "FAAC 2.1.0"),
    "2.2": ("faac-2.2", "6acbe23ac9318d8f731ef38e0a56365f70af4797", "meson", "FAAC 2.2.0 (6acbe23)"),
}


def tag_problems():
    """For check_sources.py: the repositories that do not serve a tag with its pinned commit."""
    problems = []
    for tag, commit, _, _ in RELEASES.values():
        for repository in REPOSITORIES:
            result = subprocess.run(["git", "ls-remote", repository, f"refs/tags/{tag}", f"refs/tags/{tag}^{{}}"], capture_output=True, text=True)
            if result.returncode != 0:
                problems.append(f"{repository} {tag}: {result.stderr.strip().splitlines()[-1] if result.stderr.strip() else f'git exit {result.returncode}'}")
                continue
            # An annotated tag lists itself and, with ^{}, its commit; a lightweight tag lists its commit.
            named = [line.split()[0] for line in result.stdout.splitlines()]
            if not named or named[-1] != commit:
                problems.append(f"{repository} {tag}: names {named[-1] if named else 'nothing'}, expected {commit}")
    return problems


def build(version):
    """Builds FAAC `version` in a new temporary directory and returns the front end, a copy that depends on
    nothing else in it: the directory is the binary's parent, to remove when done."""
    tag, commit, system, banner = RELEASES[version]
    root = pathlib.Path(tempfile.mkdtemp(prefix=f"faac-{version}-"))
    source = root / "src"
    for repository in REPOSITORIES:
        if subprocess.run(["git", "clone", "-q", repository, str(source)], capture_output=True).returncode == 0:
            break
        shutil.rmtree(source, ignore_errors=True)
    else:
        raise SystemExit(f"no repository serves FAAC {version}")
    subprocess.run(["git", "-C", str(source), "checkout", "-q", commit], check=True)
    if system == "autotools":
        subprocess.run(["./bootstrap"], cwd=source, check=True, capture_output=True)
        subprocess.run(["./configure", "--disable-shared"], cwd=source, check=True, capture_output=True)
        subprocess.run(["make", "-j4"], cwd=source, check=True, capture_output=True)
        built = source / "frontend" / "faac"
    else:
        environment = dict(os.environ, GIT_CONFIG_COUNT="1", GIT_CONFIG_KEY_0="core.abbrev", GIT_CONFIG_VALUE_0="7")
        build_directory = root / "build"
        subprocess.run(["meson", "setup", str(build_directory), str(source), "-Ddefault_library=static", "--buildtype=release"], check=True,
                       capture_output=True, env=environment)
        subprocess.run(["ninja", "-C", str(build_directory)], check=True, capture_output=True, env=environment)
        built = build_directory / "frontend" / "faac"
    binary = root / "faac"
    shutil.copy2(built, binary)
    shutil.rmtree(source, ignore_errors=True)
    shutil.rmtree(root / "build", ignore_errors=True)
    result = subprocess.run([str(binary), "-h"], capture_output=True, text=True)
    if banner not in result.stdout + result.stderr:
        raise SystemExit(f"{binary} is not {banner}")
    return binary

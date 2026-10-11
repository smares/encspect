"""Builds LAME releases (LGPL) other than the system's 3.100 from source, for encoder-written fixtures
of the LAME tag as its first versions and its releases after 3.100 wrote it.

LAME 3.90.1, 3.91, 3.92 and 3.93.1 come from their release tarballs on SourceForge, each checked against a
pinned SHA-256. They write the LAME tag (since 3.90) with both ReplayGain fields zero: 3.93.1 still
marks them "TODO" (libmp3lame/VbrTag.c). The 3.90.1 tarball holds sources that call themselves 3.90
and write "LAME3.90". 3.90 to 3.92 have Dibrom's --alt-presets but no preset field in the tag, which
3.93 added; HydrogenAudio's later 3.90.2 and 3.90.3 releases of the 3.90 line have no surviving
source release that could be checked here. 3.94 was only ever released as betas, which no longer have
a tarball; the LAME project's Subversion repository keeps the tag lame3_94beta (3.94 beta 1), whose
files are fetched one by one at a pinned revision and checked against a digest of the whole tree.
It is the release that computes ReplayGain into the tag against 83 dB: its gain_analysis.c lowers
PINK_REF from 64.82 to 58.82, which 3.95 reverted, so its gains read 6 dB lower than those of
later versions.

LAME 3.101 and 4.0 (July 2026) come from their release tarballs too. 3.101 has no room left for the
release marker in the tag's nine bytes of version, "LAME3.101"; 4.0 bumped the major version to
make room and writes "LAME4.0 " and a NUL byte. Both compute the tag's CRCs with a new
slicing-by-8 routine, which gives the same values.

3.90.1, 3.91 and 3.92 predate a libtool that knows x86-64, so they are built with the Makefile.unix their
releases include (3.92's misses presets.c); 3.94 beta 1, 3.101 and 4.0 with their configure scripts.
"""

import hashlib
import pathlib
import re
import subprocess
import tarfile
import tempfile
import urllib.request

import downloads

RELEASES = {
    "3.90.1": "3c582092c3eb781f02b5febf10e4759be00c27888ecfe58cb11ba8b2605212ed",
    "3.91": "55c54edd5d819699dad68949e6080358a2c6e390a118c0f2b57c09950d63050f",
    "3.92": "303c1e5bffa2f3358629d5022e910cb48e9691d6b8d923ad7d649a41c7df8100",
    "3.93.1": "cc19feb74b7b194667719af438f7cef361acc24f9c3f7c348f12e08d071fa65d",
    "3.101": "7578af6eebd578b2bd64e468fac4ae1f03670a7e028166e67f855674b9b6aeac",
    "4.0": "3df5124d5ad3a98312ffd7ba6a9b36230e4f8a3e66d3ce0f425e336c32d216eb",
}
BANNERS = {"3.90.1": "LAME version 3.90 ", "3.91": "LAME version 3.91 ", "3.92": "LAME version 3.92 ", "3.93.1": "LAME version 3.93 ", "3.94b1": "LAME version 3.94 (beta 1",
           "3.101": "version 3.101 (", "4.0": "version 4.0 ("}

# svn's HTTP interface lists a directory as HTML; "!svn/bc/<revision>" fixes the revision.
BETA_URL = "https://svn.code.sf.net/p/lame/svn/!svn/bc/7051/tags/lame3_94beta/lame/"
BETA_TREE_SHA256 = "c928fb4faa8f9de9b5acfc09c08fab88088079fbf2240c6681223c162fa78e0a"

CFLAGS = "-O2 -w -fcommon -std=gnu89"

# The releases after 3.100, built with their own configure scripts.
CONFIGURED = ("3.101", "4.0")


def addresses(version):
    return [f"https://downloads.sourceforge.net/project/lame/lame/{version}/lame-{version}.tar.gz",
            f"https://sourceforge.net/projects/lame/files/lame/{version}/lame-{version}.tar.gz/download"]


def tree_digest(directory):
    """SHA-256 over every file's path and SHA-256, in path order."""
    digest = hashlib.sha256()
    for path in sorted(p for p in directory.rglob("*") if p.is_file()):
        digest.update(f"{path.relative_to(directory).as_posix()}\0{hashlib.sha256(path.read_bytes()).hexdigest()}\n".encode())
    return digest.hexdigest()


def fetch_tree(url, directory):
    """Downloads the files under the svn directory url into directory."""
    with urllib.request.urlopen(url, timeout=60) as response:
        listing = response.read().decode("utf-8", "replace")
    for name in re.findall(r'<li><a href="([^"]+)">', listing):
        if name == "../":
            continue
        if name.endswith("/"):
            (directory / name).mkdir(parents=True, exist_ok=True)
            fetch_tree(url + name, directory / name)
        else:
            with urllib.request.urlopen(url + name, timeout=60) as response:
                (directory / name).write_bytes(response.read())


def build(version):
    """Builds the lame front end of version ("3.90.1", "3.91", "3.92", "3.93.1", "3.94b1", "3.101" or "4.0")
    in a temporary directory and returns its path."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix=f"lame-{version}-"))
    if version == "3.94b1":
        source = directory / "lame"
        source.mkdir()
        fetch_tree(BETA_URL, source)
        if tree_digest(source) != BETA_TREE_SHA256:
            raise SystemExit(f"{BETA_URL} holds a tree with digest {tree_digest(source)}, expected {BETA_TREE_SHA256}")
        for script in ("configure", "config.guess", "config.sub", "install-sh", "depcomp", "missing", "mkinstalldirs", "ltmain.sh"):
            if (source / script).exists():
                (source / script).chmod(0o755)
        subprocess.run(["./configure", "--disable-shared", "--enable-static", "--without-vorbis", "--disable-gtktest", f"CFLAGS={CFLAGS}"],
                       cwd=source, check=True, capture_output=True)
        subprocess.run(["make", "-j8"], cwd=source, check=True, capture_output=True)
    else:
        archive = directory / f"lame-{version}.tar.gz"
        downloads.fetch(addresses(version), RELEASES[version], archive)
        with tarfile.open(archive) as tar:
            tar.extractall(directory, filter="data")
        # The 3.90.1 tarball unpacks as lame-3.90 and its sources call themselves 3.90.
        source = directory / ("lame-3.90" if version == "3.90.1" else f"lame-{version}")
        if version in CONFIGURED:
            # Their decoder needs libmpg123, which encoding does not.
            subprocess.run(["./configure", "--disable-shared", "--disable-decoder"], cwd=source, check=True, capture_output=True)
            subprocess.run(["make", "-j8"], cwd=source, check=True, capture_output=True)
        else:
            if version == "3.92":
                makefile = source / "Makefile.unix"
                makefile.write_text(makefile.read_text().replace("libmp3lame/set_get.c \\", "libmp3lame/set_get.c libmp3lame/presets.c \\", 1))
            subprocess.run(["make", "-f", "Makefile.unix", f"CC_OPTS={CFLAGS}"], cwd=source, check=True, capture_output=True)
    binary = source / "frontend" / "lame"
    banner = subprocess.run([str(binary), "--version"], capture_output=True, text=True).stdout
    if BANNERS[version] not in banner:
        raise SystemExit(f"{binary} is not LAME {version}: {banner[:100]}")
    return binary

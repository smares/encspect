"""Builds the Musepack tools the fixtures and their cross-check need from pinned sources.

- mppenc 1.16, the last SV7 encoder, from its source release on files.musepack.net (pinned by SHA-256),
  for the SV7 fixtures. Modern GCC places its fast-math tables, which it declares const without an
  initializer and fills at startup, in read-only memory; -fcommon puts them back where the 2006 build
  had them. Nothing else is changed.
- mpcdec_frames, a small program (mpcdec_frames.c) over libmpcdec from musepack_src r495, the source
  Debian and Ubuntu build musepack-tools 0.1~r495 from (deb.debian.org, pinned by SHA-256), which
  prints what the decoder reads from each SV7 frame.

- mpcdec_frames126, the same program with -DLIBMPCDEC_126 over libmpcdec 1.2.6 (files.musepack.net,
  pinned by SHA-256), the last libmpcdec that decodes stream versions 4 to 6. Its configure step only
  writes config_types.h, which this script writes from the template with <stdint.h> types.
- mpcdec_frames_portable and mpcdec_portable, the Portable Musepack decoder ((C) 1999-2002
  Buschmann/Klemm/Piecha/Wolf, (C) 2003-2004 Peter Pawlowski, LGPL; mpc.zip from Rarewares, pinned by
  SHA-256), the C++ code libmpcdec 1.2.6 was ported to C from (its AUTHORS: "Port to pure C"; the
  two share their functions, names and comments), so the same reading of stream versions 4 to 6 in an
  earlier form, not an independent one; building it catches errors in building libmpcdec 1.2.6 or in
  this patch. The first program is mpcdec_frames_portable.cpp over its library, which prints what
  mpcdec_frames126 prints, the second the archive's own driver, sample.cpp, unchanged. Written for 32-bit Windows, it keeps 32-bit
  words in "long", which is 64 bits on 64-bit Linux, where its bit reader then fails on the first
  frame; the one patch turns every "long" that is not part of "long long" into "int", in every file the
  build uses. Its endianness switch, MPC_LITTLE_ENDIAN, is defined only for WIN32, so the build
  defines it.
- Rarewares' Win32 builds of the old encoders and decoder, which Guruboolez contributed
  (www.rarewares.org/rrw/mpegplus.php), run under Wine: the SV4 and SV5 encoders (mppsv4, mppsv5),
  every SV7 mppenc from 0.90 to 1.15u and Buschmann's MP+ 1.7.8 to 1.7.9c, and mppdec. RAREWARES pins
  each archive by SHA-256. Two of them (1.05z1, 1.05z2) lack the zip's central directory; their one
  member is read from its local header and checked against its CRC-32. The 0.90i archive holds two
  builds of 0.90f, the 1.05z3 archive a build that calls itself 1.06 alpha.

mppenc is under the LGPL 2.1 and libmpcdec under a BSD license; the Win32 builds are freeware. None of
them is redistributed.
"""

import io
import os
import pathlib
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile
import zlib

import downloads
import xing_tool

MPPENC = ("mppenc-1.16.tar.bz2", "f90781856ca7b6ce1ec7033321fadbcd04fe264d0349c20b49ad05f7cb2655ad",
          ["https://files.musepack.net/source/mppenc-1.16.tar.bz2"])
LIBMPC = ("libmpc_0.1~r495.orig.tar.gz", "5182d7a30eec1b338cafc9c1706d663a6c8dc217b1d90187054512074f662202",
          ["https://deb.debian.org/debian/pool/main/libm/libmpc/libmpc_0.1~r495.orig.tar.gz"])
LIBMPCDEC_126 = ("libmpcdec-1.2.6.tar.bz2", "4bd54929a80850754f27b568d7891e1e3e1b8d2f208d371f27d1fda09e6f12a8",
                 ["https://files.musepack.net/source/libmpcdec-1.2.6.tar.bz2"])
PORTABLE = ("mpc.zip", "5490e2fbf9a7f766a6eb85747e601f30c6178f03ddc1cee26f33ab08074a0f48", ["https://www.rarewares.org/john33/mpc.zip"])
PORTABLE_LIBRARY = ["mpc_dec.cpp", "streaminfo.cpp", "huffsv46.cpp", "huffsv7.cpp", "bitstream.cpp", "synth_filter.cpp", "requant.cpp", "idtag.cpp"]
PORTABLE_HEADERS = ["mpc_dec.h", "mpc_math.h", "requant.h", "stdafx.h", "streaminfo.h"]
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
CACHE = ROOT / ".cache" / "encoders"

RAREWARES = "https://www.rarewares.org/rrw/files/mpp/"

# Build: (archive, SHA-256, the encoder or decoder in it). Build names are the versions the binaries
# print, but for 0.90r (archive 0.90s) and 1.0 to 1.02, which print none, the archive's.
BUILDS = {
    "sv4": ("mppsv4.zip", "7c3e1c83aeb8059f525f52e3fc527e458dac4ee264666932eb61f68bf3372b1c", "mppenc.exe"),
    "sv5": ("mppsv5.zip", "847b4ff2afc6051d5ac196f508cd192f3e40cb9c6f9da44169d2ec32e73179bc", "mppenc.exe"),
    "mppdec": ("mppdec.zip", "17894c3ffea632a8222c453e8621dde76c19326fe1d9d57f13561d0f24270896", "mppdec.exe"),
    "0.90f": ("mppenc-windows-0.90i.zip", "62de218ad95e8a71a0b6329cc54975773de12122a447e8bed3a9d19cf4e9ff84", "mppenc-v1.exe"),
    "0.90f (v2)": ("mppenc-windows-0.90i.zip", "62de218ad95e8a71a0b6329cc54975773de12122a447e8bed3a9d19cf4e9ff84", "mppenc-v2.exe"),
    "0.90k": ("mppenc-windows-0.90k.zip", "94371b923c20db95ba762030abc5b8dd12697d574aaaba842b2135e67d3b556a", "mppenc-windows-0.90k/mppenc.exe"),
    "0.90m": ("mppenc-windows-0.90m.zip", "87715b8b0959d37be818aa039724c7875a89341f11cf2a1513180d0de8523b02", "mppenc.exe"),
    "0.90o": ("mppenc-windows-0.90o.zip", "352ad6af722e23d1eee1d1242e609f8e348c3f9a6d8656326b594ce67dddd9ff", "mppenc.exe"),
    "0.90r": ("mppenc-windows-0.90s.zip", "75558fe37d8c939ba0073e7d550a475d1497d8b4079d19486f37990872aba4be", "mppenc.exe"),
    "1.0": ("mppenc-windows-1.0.zip", "510fb9788562fc475b22a636e538bbad18870bf3ca7ccbd097cf777cb69d4543", "mppenc.exe"),
    "1.01j": ("mppenc-windows-1.01j.zip", "972c061395d7d3fbfac21ce34d98fcdf925665d4dd8b2021297d18ebe431a9d4", "mppenc.exe"),
    "1.02": ("mppenc-windows-1.02.zip", "433989e6eb84b2271dabed60ce62411d78b8a28e524d0d6a2abb877a0245e35f", "mppenc.exe"),
    "1.04": ("mppenc-windows-1.04.zip", "13a243ca195feab8bd23f4789a3cab0b8f7bdf81a7a50a70b3cb0713fef03941", "mppenc.exe"),
    "1.05z1": ("mppenc-windows-1.05z1.zip", "c2f2d438ca2758f148188f7d41de71b151b6bed09865428e9adef773c16c66e8", "mppenc.exe"),
    "1.05z2": ("mppenc-windows-1.05z2.zip", "106c14da5a966e7d7606e94cfaac025659e3e6979d45b76e4a481243e664f1ae", "mppenc.exe"),
    "1.06 alpha": ("mppenc-windows-1.05z3.zip", "1bd4f7e3bc315ce642c37bda62d5bcfc00c326f1ad5f711b6afdfb8d61e8d396", "mppenc.exe"),
    "1.06": ("mppenc-windows-1.06.zip", "75ca0cf93c51072b3806e2bbc57ac581c0bebe24f9f8bcd76ab55a6811fb377f", "mppenc.exe"),
    "1.1": ("mppenc-windows-1.1.zip", "d971374df7a650d17e6e4d52e75f1e4ad7aa7480819c425749ad78eec5d64bed", "mppenc.exe"),
    "1.11a": ("mppenc-windows-1.11a.zip", "91d5cd1d1fbd52a95ac98791ead996d18c0f89b3f923d04425d716bb3c06b97f", "mppenc.exe"),
    "1.11b": ("mppenc-windows-1.11b.zip", "a2ce2278586ad11ccf96a87ed4446964a2da78311d8a5fb7bb9a7a967efece90", "mppenc.exe"),
    "1.12": ("mppenc-windows-1.12.zip", "0d68cb1175434e7574e1d230a443028de5ce45074e50935d556546451dbdba23", "mppenc.exe"),
    "1.13a": ("mppenc-windows-1.13a.zip", "d4b09919d918b3b0998efad8e0492a1aad7fc68bb802235a399f66e620c8338e", "mppenc.exe"),
    "1.13g": ("mppenc-windows-1.13g.zip", "ba1099385b768f4cf7a844552bc3c5a0b8fe645d62955878d353d6065d1b39e7", "mppenc.exe"),
    "1.14": ("mppenc-windows-1.14.zip", "125378428e41be540d13feae212a1db025ad6ad44f5bfe19029e6b8fa2b58efc", "mppenc.exe"),
    "1.15f": ("mppenc-windows-1.15f.zip", "58a827193bb5c39bff3f0f74e46a37890b97c57ac84c34f4293360452c2539cd", "mppenc.exe"),
    "1.15i": ("mppenc-windows-1.15i.zip", "b1ce9d6167cccb4e0804db65f3bcc4dd751116acfa2112adc7ce7733ddab3768", "mppenc.exe"),
    "1.15r": ("mppenc-windows-1.15r.zip", "095d5fd966805a68e512bc0b98783ef76dd8e4ecac792eb31c5be4d8c0fa6a92", "mppenc.exe"),
    "1.15t": ("mppenc-windows-1.15t.zip", "862e5c370c0f0d16e7f08e22e3655dfd10a7f22aa2aa66a7f1783e3f262d55de", "mppenc.exe"),
    "1.15u": ("mppenc-windows-1.15u.zip", "15fa000c2a503c97972d3304669c5487bf1b3000ac81c1d61d7c0c642509ab62", "mppenc.exe"),
    "MP+ 1.7.8": ("mppenc_1_7_8.zip", "5ecf4891cb00d4576eb6e0efb070062e4aedaec471d9d0cb8156ff65d7bba463", "mppenc.exe"),
    "MP+ 1.7.9": ("mppenc_1_7_9.zip", "783f778c57ef6107008bd1519dfd00f376130a7d77ae6b41be772bacd48cf8e2", "mppenc.exe"),
    "MP+ 1.7.9a": ("mppenc_1_7_9a.zip", "f6f59e9f45f6c4a1661a97850d850774307e8f7e261f2a9a3c2c719bd2546871", "mppenc.exe"),
    "MP+ 1.7.9c": ("mppenc_1_7_9c.zip", "b92aeb6a2deacc8210241a9edd6a6c5338f7148ed35535b52fd26a5d64d4f1f0", "mppenc.exe"),
}

# The SV7 builds, which take a WAVE file and an output name; the others need switches of their own.
SV7_BUILDS = [name for name in BUILDS if name not in ("sv4", "sv5", "mppdec")]


def fetch(spec, directory):
    name, digest, addresses = spec
    path = directory / name
    downloads.fetch(addresses, digest, path)
    return path


def extract(archive, directory):
    with tarfile.open(archive) as tar:
        tar.extractall(directory, filter="data")


def build_mppenc(directory=None):
    """The path of an mppenc 1.16 binary, built in directory (a new temporary one by default)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="mppenc-"))
    directory.mkdir(parents=True, exist_ok=True)
    extract(fetch(MPPENC, directory), directory)
    source = directory / "mppenc-1.16" / "src"
    binary = directory / "mppenc"
    subprocess.run(["gcc", "-O2", "-ffast-math", "-fomit-frame-pointer", "-fcommon", "-DMPP_ENCODER", "-DFAST_MATH", "-DCVD_FASTLOG", "-w",
                    "-o", str(binary), *sorted(str(p) for p in source.glob("*.c")), "-lm"], check=True)
    banner = subprocess.run([str(binary)], capture_output=True, text=True).stderr
    if "MPC Encoder  1.16" not in banner:
        raise SystemExit(f"{binary} is not mppenc 1.16")
    return binary


def build_frames(directory=None):
    """The path of mpcdec_frames, built in directory (a new temporary one by default)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libmpcdec-"))
    directory.mkdir(parents=True, exist_ok=True)
    extract(fetch(LIBMPC, directory), directory)
    source = directory / "musepack_src_r495"
    library = source / "libmpcdec"
    files = ["huffman.c", "mpc_bits_reader.c", "mpc_decoder.c", "mpc_demux.c", "mpc_reader.c", "requant.c", "streaminfo.c", "synth_filter.c"]
    binary = directory / "mpcdec_frames"
    subprocess.run(["gcc", "-O1", "-w", "-I", str(source / "include"), "-I", str(library), "-o", str(binary), str(HERE / "mpcdec_frames.c"),
                    *(str(library / f) for f in files), str(source / "common" / "crc32.c"), "-lm"], check=True)
    return binary


def rarewares_addresses(archive):
    return [RAREWARES + archive]


def sources():
    """(archive, SHA-256, addresses) of every archive this script downloads, once each."""
    seen = set()
    for spec in (MPPENC, LIBMPC, LIBMPCDEC_126, PORTABLE):
        yield spec
    for archive, digest, _ in BUILDS.values():
        if archive not in seen:
            seen.add(archive)
            yield archive, digest, rarewares_addresses(archive)


def unzip(path, member):
    """The bytes of member in the zip at path. Where the central directory is missing, the local
    headers are read in order and the member's data inflated and checked against its CRC-32."""
    data = path.read_bytes()
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as files:
            return files.read(member)
    except zipfile.BadZipFile:
        pass
    position = 0
    while (position := data.find(b"PK\x03\x04", position)) >= 0:
        method, crc, compressed, size, name_length, extra_length = struct.unpack("<8xH4xIIIHH", data[position:position + 30])
        name = data[position + 30:position + 30 + name_length].decode("latin-1")
        start = position + 30 + name_length + extra_length
        if name == member:
            raw = data[start:start + compressed] if compressed else data[start:]
            content = raw if method == 0 else zlib.decompressobj(-15).decompress(raw)
            if len(content) != size or zlib.crc32(content) != crc:
                raise SystemExit(f"{path.name}: {member} does not match its CRC-32")
            return content
        position = start
    raise SystemExit(f"{path.name} holds no {member}")


def windows_binary(build, directory):
    """The build's executable, written into directory."""
    archive, digest, member = BUILDS[build]
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / archive
    downloads.fetch(rarewares_addresses(archive), digest, path)
    target = pathlib.Path(directory) / pathlib.Path(member).name
    target.write_bytes(unzip(path, member))
    return target


def wine(binary, arguments, directory):
    environment = dict(os.environ, WINEPREFIX=str(xing_tool.WINE_PREFIX), WINEDEBUG="-all")
    # Wine crashes at start where TMPDIR names a long path.
    environment.pop("TMPDIR", None)
    return subprocess.run(["wine", binary.name, *arguments], cwd=directory, env=environment, capture_output=True, text=True, errors="replace",
                          stdin=subprocess.DEVNULL, timeout=600)


def encode_windows(build, arguments, source, target):
    """Encodes the WAVE file source with a Rarewares build under Wine into target. The SV4 and SV5
    encoders take the switches after the file names; they and Buschmann's MP+ builds ignore an output
    name without ".mp+"."""
    with tempfile.TemporaryDirectory() as temp:
        binary = windows_binary(build, temp)
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        output = "out.mp+" if build in ("sv4", "sv5") or build.startswith("MP+") else "out.mpc"
        wine(binary, ["in.wav", output, *arguments] if build in ("sv4", "sv5") else [*arguments, "in.wav", output], temp)
        written = pathlib.Path(temp) / output
        if not written.exists() or written.stat().st_size == 0:
            raise SystemExit(f"{build} {' '.join(arguments)} wrote nothing")
        shutil.move(written, target)


def mppdec_line(path):
    """The line mppdec prints when it has decoded the stream at path, under Wine ("EOF reached after N
    Frames or ... hms"), "" where it prints none, or None where Wine is missing."""
    if shutil.which("wine") is None:
        return None
    with tempfile.TemporaryDirectory() as temp:
        binary = windows_binary("mppdec", temp)
        shutil.copy(path, pathlib.Path(temp) / "in.mpc")
        result = wine(binary, ["in.mpc", "out.wav"], temp)
        found = re.search(r"EOF reached after \d+ Frames[^\n]*", result.stdout)
        return found.group(0).strip() if found else ""


def mppdec_frames(path):
    """The frames mppdec decodes from the stream at path, under Wine, as it reports them ("EOF reached
    after N Frames"), or None where Wine is missing. Its WAVE file is no count: it rounds the data up
    (32768 samples for 28 frames)."""
    line = mppdec_line(path)
    if line is None:
        return None
    found = re.search(r"after (\d+) Frames", line)
    return int(found.group(1)) if found else 0


def build_frames_126(directory=None):
    """The path of mpcdec_frames126, built in directory (a new temporary one by default)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libmpcdec126-"))
    directory.mkdir(parents=True, exist_ok=True)
    extract(fetch(LIBMPCDEC_126, directory), directory)
    source = directory / "libmpcdec-1.2.6"
    template = (source / "include" / "mpcdec" / "config_types.h.in").read_text()
    for marker, value in (("@INTTYPES_H@", "#include <stdint.h>"), ("@SIZE16@", "int16_t"), ("@USIZE16@", "uint16_t"), ("@SIZE32@", "int32_t"),
                          ("@USIZE32@", "uint32_t"), ("@SIZE64@", "int64_t")):
        template = template.replace(marker, value)
    (source / "include" / "mpcdec" / "config_types.h").write_text(template)
    files = ["mpc_decoder.c", "huffsv46.c", "huffsv7.c", "mpc_reader.c", "requant.c", "synth_filter.c", "idtag.c"]
    binary = directory / "mpcdec_frames126"
    subprocess.run(["gcc", "-O1", "-w", "-DLIBMPCDEC_126", "-DMPC_SUPPORT_SV456", "-DMPC_LITTLE_ENDIAN", "-I", str(source / "include"), "-I",
                    str(source / "src"), "-o", str(binary), str(HERE / "mpcdec_frames.c"), *(str(source / "src" / f) for f in files), "-lm"], check=True)
    return binary


def build_portable(directory=None):
    """The paths of mpcdec_frames_portable and mpcdec_portable, built in directory (a new temporary one
    by default) from the patched archive."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="mpc-portable-"))
    directory.mkdir(parents=True, exist_ok=True)
    archive = fetch(PORTABLE, directory)
    source = directory / "mpc"
    source.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive) as files:
        for member in [*PORTABLE_LIBRARY, *PORTABLE_HEADERS, "sample.cpp"]:
            text = files.read(member).decode("latin-1")
            (source / member).write_bytes(re.sub(r"(?<!long )\blong\b(?! long)", "int", text).encode("latin-1"))
    library = [str(source / f) for f in PORTABLE_LIBRARY]
    flags = ["g++", "-O1", "-w", "-DMPC_LITTLE_ENDIAN", "-I", str(source)]
    frames = directory / "mpcdec_frames_portable"
    decoder = directory / "mpcdec_portable"
    subprocess.run([*flags, "-o", str(frames), str(HERE / "mpcdec_frames_portable.cpp"), *library], check=True)
    subprocess.run([*flags, "-o", str(decoder), str(source / "sample.cpp"), *library], check=True)
    return frames, decoder


if __name__ == "__main__":
    print(build_mppenc() if sys.argv[1:] == ["mppenc"] else build_frames())

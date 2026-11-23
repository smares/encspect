"""Fraunhofer IIS's MP3 ACM codecs, driven by nyaochi's acmenc 0.6 under Wine, for encoder-written fixtures.

The codecs are the source of most Fraunhofer-written MP3 files made on Windows (the Audio Compression
Manager's MP3 encoder, which CD rippers, editors and Windows Media Player called):
  - l3codeca 1.9.0.306 ("advanced", 8 to 56 kbps, 1999, shipped with JRiver Media Jukebox 5),
  - l3codecp 3.3.2.44 ("professional", 8 to 320 kbps, 2005, shipped with Windows Media Player 10),
  - l3codecp 3.4 ("professional", 2006, shipped with Windows Media Player 11).
Rarewares' ReallyRareWares keeps each as a self-extracting cabinet, which 7z unpacks. They carry no
license text. acmenc (Nyaochi, zlib license, with LGPL libsndfile and id3lib DLLs) reads the WAV file,
hands it to the ACM codec the registry names, and always writes an Info frame in front: its fields
(frame count, size, table of contents, delay and padding) are acmenc's, and so is the vendor string
that --tv sets; the presets of its acmenc.cfg set "FIIS3.3" and "FIIS1.9". Without --tv the string is
empty. The frames after it are the codec's.

Wine's own l3codeca.acm (a decoder over mpg123) bears the name of Fraunhofer's, so each codec is
copied under another name into C:\\fhg of its own Wine prefix and registered as msacm.l3acm (in the
32-bit registry view, which the 32-bit acmenc reads), the one entry the three share; one codec is
registered at a time. The prefix lives outside the repository, as xing_tool.py's does, and is made
on first use (a minute or two). Wine's heap breaks when TMPDIR is set, so it is removed.
"""

import hashlib
import io
import os
import pathlib
import shutil
import subprocess
import tempfile
import zipfile

import downloads

CACHE = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "encoders"
PREFIX = pathlib.Path(tempfile.gettempdir()) / "encspect-wine-acm"
BASE = "https://www.rarewares.org/rrw/files/"

ACMENC = ("acmenc-0.6.zip", "4c0b172f61ec97912b9854f108bf6273acd02c62b023731a95013bccb04baea5", BASE + "others/acmenc-0.6.zip")
ACMENC_FILES = {"acmenc.exe": "d30a6e329a69148d8ae52a9a04b950375b532ff9e640528bc892e7bdc5407d7b",
                "libsndfile.dll": "08f9841e969d6c2451e94b9f6af7172765a643a5c84d4ed444ecd122db9cf205",
                "id3lib.dll": "3c16f05c4fb3a4fb6ac98ee70edcd5f6d790827b43641bdbdfec52aaca2291af",
                "acmenc.cfg": "f7fe977159b44724797f88e26e8f20d48a95d9072ac301aa84748d11ab3ddf05"}

# Version -> installer, its SHA-256, the codec inside it, that file's SHA-256, the codec's long name and its "Version" in -ld.
CODECS = {
    "1.9": dict(archive="l3codeca.exe", sha256="62d6075b863d957b0d0af8df579d70331e132cef23562bf7d725b32d40e39eac",
                address=BASE + "fhg/l3codeca.exe", member="l3codeca.acm",
                member_sha256="ffcd2b9e6401d2614830999ea24eda2c48f8bf4fd3b2f9ad95eba89513080fe3",
                name="Fraunhofer IIS MPEG Layer-3 Codec (advanced)", version="1.9.306"),
    "3.3": dict(archive="l3codecp-3.3.exe", sha256="a0b0117f9012627e07ab309fb9c4997b2b640081841aa565692c03ded54a542e",
                address=BASE + "fhg/l3codecp-3.3.exe", member="L3CODECP.ACM",
                member_sha256="0805dc6c6f23dbf407454d8e8822077d97e25d558f898a62bf0528cceaf95162",
                name="Fraunhofer IIS MPEG Layer-3 Codec (professional)", version="3.3.44"),
    "3.4": dict(archive="l3codecp-3.4.exe", sha256="766ba3b215429c620c59238576ef320589d71bf91be6003b43b8142036f80462",
                address=BASE + "fhg/l3codecp-3.4.exe", member="l3codecp.acm",
                member_sha256="d36c439ebf1801c060a7dec668dbcac35091a83c8334462f55b0e2c419c6f4ce",
                name="Fraunhofer IIS MPEG Layer-3 Codec (professional)", version="3.4.0"),
}
DRIVERS32 = r"HKLM\Software\Microsoft\Windows NT\CurrentVersion\Drivers32"


def environment():
    return {k: v for k, v in os.environ.items() if k != "TMPDIR"} | {"WINEPREFIX": str(PREFIX), "WINEDEBUG": "-all",
                                                                              "WINEDLLOVERRIDES": "mscoree,mshtml="}


def wine(arguments, directory, timeout=600):
    return subprocess.run(["wine", *arguments], cwd=directory, env=environment(), capture_output=True, text=True,
                          stdin=subprocess.DEVNULL, timeout=timeout, errors="replace")


def checked(data, digest, name):
    if hashlib.sha256(data).hexdigest() != digest:
        raise SystemExit(f"{name} has SHA-256 {hashlib.sha256(data).hexdigest()}, expected {digest}")
    return data


def fetch(versions):
    """Downloads and checks acmenc and the codecs of versions, unpacks them into .cache/encoders/fhg-acm,
    makes the Wine prefix if it is missing, and returns the directory."""
    CACHE.mkdir(parents=True, exist_ok=True)
    directory = CACHE / "fhg-acm"
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir()
    name, digest, address = ACMENC
    downloads.fetch([address], digest, CACHE / name)
    with zipfile.ZipFile(io.BytesIO((CACHE / name).read_bytes())) as files:
        for member, member_digest in ACMENC_FILES.items():
            (directory / member).write_bytes(checked(files.read(member), member_digest, member))
    for version in versions:
        codec = CODECS[version]
        installer = CACHE / codec["archive"]
        downloads.fetch([codec["address"]], codec["sha256"], installer)
        with tempfile.TemporaryDirectory() as temp:
            subprocess.run(["7z", "x", "-y", f"-o{temp}", str(installer), codec["member"]], check=True, capture_output=True)
            (directory / f"fhg{version}.acm").write_bytes(checked((pathlib.Path(temp) / codec["member"]).read_bytes(), codec["member_sha256"], codec["member"]))
    if not (PREFIX / "system.reg").exists():
        wine(["wineboot", "-i"], directory, 1800)
    (PREFIX / "drive_c" / "fhg").mkdir(parents=True, exist_ok=True)
    return directory


INSTALLED = []


def install(directory, version):
    """Registers the codec of version as the MP3 ACM driver and checks that acmenc lists it, once per process for each switch."""
    if INSTALLED[-1:] == [version]:
        return
    codec = CODECS[version]
    shutil.copy(directory / f"fhg{version}.acm", PREFIX / "drive_c" / "fhg" / f"fhg{version}.acm")
    result = wine(["reg", "add", DRIVERS32, "/v", "msacm.l3acm", "/d", f"C:\\fhg\\fhg{version}.acm", "/f", "/reg:32"], directory)
    if result.returncode != 0:
        raise SystemExit(f"registering the codec failed: {result.stdout[-300:]}{result.stderr[-300:]}")
    listing = wine([str(directory / "acmenc.exe"), "-ld"], directory).stdout
    if f"MPEG Layer-3 Codec Version {codec['version']} {codec['name']}" not in " ".join(listing.split()):
        raise SystemExit(f"acmenc does not list the {version} codec: {listing[:400]}")
    INSTALLED.append(version)


def encode(directory, version, arguments, source, path):
    """Encodes source to path with the codec of version, from a temporary directory next to acmenc's DLLs."""
    install(directory, version)
    with tempfile.TemporaryDirectory() as temp:
        for name in ACMENC_FILES:
            shutil.copy(directory / name, pathlib.Path(temp) / name)
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        result = wine([str(pathlib.Path(temp) / "acmenc.exe"), "-c", CODECS[version]["name"], *arguments, "in.wav", "out.mp3"], temp)
        output = pathlib.Path(temp) / "out.mp3"
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"acmenc with l3codec {version} {' '.join(arguments)} failed: {result.stdout[-300:]}")
        shutil.move(output, path)

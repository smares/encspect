"""Builds the Fraunhofer FDK AAC library and the fdkaac front end for encoder-written fixtures.

Ubuntu packages fdkaac 1.0.0, but its libfdk-aac2 lacks SBR and PS, so HE-AAC and HE-AAC v2 fail with
"unsupported profile". This builds libfdk-aac 2.0.2 (library version 4.0.1, the one Ubuntu ships) from
github.com/mstorsjo/fdk-aac at its tag's commit, and fdkaac 1.0.0 from github.com/nu774/fdkaac at its
tag's commit, compiled directly with gcc instead of its autotools. fdkaac picks the SBR signaling for
MP4 itself (src/main.c: explicit backward-compatible signaling, the sync extension 0x2B7); one line added
after that choice lets FDKAAC_SBR_SIGNALING override it with the library's AACENC_SIGNALING_MODE (2:
explicit hierarchical, object type 5 or 29 first, the mode ffmpeg's libfdk_aac calls
explicit_hierarchical), so the hierarchical fixtures come from the same encoder.

The library's decoder also checks ADTS CRCs (fdk_crc.c), which no Ubuntu package does: crc_errors()
lists the access units it rejects with AAC_DEC_CRC_ERROR.

The FDK AAC library is under the Fraunhofer FDK AAC Codec Library license and fdkaac under its zlib-style
license; both are built for encoding this project's own test signals and not redistributed.
"""

import pathlib
import subprocess
import tempfile

FDK_REPOSITORY = "https://github.com/mstorsjo/fdk-aac"
FDK_COMMIT = "801f67f671929311e0c9952c5f92d6e147c7b003"  # v2.0.2
FDKAAC_REPOSITORY = "https://github.com/nu774/fdkaac"
FDKAAC_COMMIT = "f2f9c1b4bbc0c6c579dc79a2ce5277d2dc505551"  # 1.0.0
VERSION = "fdkaac 1.0.0"

SOURCES = [
    "aacenc.c", "caf_reader.c", "compat_posix.c", "extrapolater.c", "limiter.c", "lpc.c", "m4af.c", "main.c", "metadata.c", "parson.c",
    "pcm_float_converter.c", "pcm_native_converter.c", "pcm_readhelper.c", "pcm_sint16_converter.c", "progress.c", "wav_reader.c",
]

PATCH_AFTER = "            params.sbr_signaling = 1;\n    }\n"
PATCH = '    if (getenv("FDKAAC_SBR_SIGNALING"))\n        params.sbr_signaling = atoi(getenv("FDKAAC_SBR_SIGNALING"));\n'


def clone(repository, commit, directory):
    subprocess.run(["git", "clone", "-q", repository, str(directory)], check=True)
    subprocess.run(["git", "-C", str(directory), "checkout", "-q", commit], check=True)


def build():
    """Returns the fdkaac binary and the directory of the library it needs on LD_LIBRARY_PATH."""
    root = pathlib.Path(tempfile.mkdtemp(prefix="fdk-"))
    fdk = root / "fdk-aac"
    clone(FDK_REPOSITORY, FDK_COMMIT, fdk)
    subprocess.run(["cmake", "-S", str(fdk), "-B", str(fdk / "build"), "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED_LIBS=ON", "-DBUILD_PROGRAMS=OFF"],
                   check=True, capture_output=True)
    subprocess.run(["cmake", "--build", str(fdk / "build"), "-j4"], check=True, capture_output=True)
    include = root / "include" / "fdk-aac"
    include.mkdir(parents=True)
    for header in [*fdk.glob("libAACenc/include/*.h"), *fdk.glob("libAACdec/include/*.h"), *fdk.glob("libSYS/include/*.h")]:
        (include / header.name).write_bytes(header.read_bytes())

    front = root / "fdkaac"
    clone(FDKAAC_REPOSITORY, FDKAAC_COMMIT, front)
    main = front / "src" / "main.c"
    text = main.read_text(encoding="utf-8")
    if PATCH_AFTER not in text:
        raise SystemExit("fdkaac's main.c no longer has the SBR signaling choice the patch follows")
    main.write_text(text.replace(PATCH_AFTER, PATCH_AFTER + PATCH, 1), encoding="utf-8")
    binary = front / "fdkaac"
    defines = ["-DHAVE_STDINT_H=1", "-DHAVE_INTTYPES_H=1", "-DHAVE_ICONV=1", "-DHAVE_LANGINFO_H=1", "-DHAVE_SIGACTION=1", "-DHAVE_FSEEKO=1",
               '-DPACKAGE_VERSION="1.0.0"']
    subprocess.run(["gcc", "-O2", "-w", *defines, "-I.", "-Isrc", f"-I{root / 'include'}", *[f"src/{s}" for s in SOURCES], f"-L{fdk / 'build'}",
                    "-lfdk-aac", "-lm", "-o", str(binary)], cwd=front, check=True)
    checker = root / "fdk_crc"
    subprocess.run(["gcc", "-O2", "-w", f"-I{root / 'include'}", str(pathlib.Path(__file__).with_name("fdk_crc.c")), f"-L{fdk / 'build'}", "-lfdk-aac",
                    "-o", str(checker)], check=True)
    library = fdk / "build"
    result = subprocess.run([str(binary)], capture_output=True, text=True, env={"LD_LIBRARY_PATH": str(library)})
    if VERSION not in result.stdout + result.stderr:
        raise SystemExit(f"{binary} is not {VERSION}")
    return binary, library, checker


def crc_errors(checker, library, path):
    """The indexes of the access units fdk-aac's decoder rejects for their ADTS CRC."""
    result = subprocess.run([str(checker), str(path)], capture_output=True, text=True, env={"LD_LIBRARY_PATH": str(library)}, check=True)
    return [int(line.split()[1].rstrip(":")) for line in result.stdout.splitlines() if line.endswith("CRC error")]

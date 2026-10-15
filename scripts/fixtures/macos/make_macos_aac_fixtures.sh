#!/bin/bash
# Encodes the project's test signals with Apple's encoders through afconvert (AudioToolbox) and
# writes the files with their source PCM and a record of the run into the directory given.
#
#     bash scripts/fixtures/macos/make_macos_aac_fixtures.sh apple-out
#
# Runs on macOS only, with the system's /bin/bash (3.2) and python3. Every afconvert call is a
# separate command that cannot stop the others: its output, exit status and command line are
# recorded and the script goes on. It fails at the end only when a required encode is missing.
#
# The directory holds:
#   pcm/             the source files (macos_aac_support.py signals), sources.json
#   encoded/         the files afconvert wrote
#   info/            afinfo's reading of each
#   logs/            afconvert's output per encode
#   environment.txt  sw_vers, the hardware, the versions of the AudioToolbox and CoreAudio frameworks and codec bundles
#   afconvert-help.txt, afconvert-formats.txt   afconvert -h and -hf, in full
#   listed.txt       which of the IDs used below the -hf output names
#   commands.txt     the exact command line of every encode, in the order run
#   encodes.tsv, manifest.json   the same record as data
#   problems.txt     only when the run failed

set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:?usage: make_macos_aac_fixtures.sh <out directory>}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
mkdir -p "$OUT/pcm" "$OUT/encoded" "$OUT/info" "$OUT/logs"
TSV="$OUT/encodes.tsv"
COMMANDS="$OUT/commands.txt"
: > "$TSV"
: > "$COMMANDS"
LIMIT=120

say() { echo "$@"; }

# ---- the environment ---------------------------------------------------------------------------
environment() {
    echo "date (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "runner image: ${ImageOS:-unknown} ${ImageVersion:-unknown}"
    echo "--- sw_vers"
    sw_vers
    echo "--- uname -a"
    uname -a
    echo "--- hardware"
    sysctl -n machdep.cpu.brand_string 2>&1
    sysctl -n hw.model hw.machine hw.optional.arm64 2>&1
    echo "--- afconvert"
    command -v afconvert
    ls -l "$(command -v afconvert)"
    file "$(command -v afconvert)"
    echo "--- python3"
    command -v python3
    python3 --version 2>&1
    echo "--- framework and codec bundle versions (CFBundleVersion / CFBundleShortVersionString)"
    for bundle in \
        /System/Library/Frameworks/AudioToolbox.framework \
        /System/Library/Frameworks/CoreAudio.framework \
        /System/Library/Frameworks/CoreAudioTypes.framework \
        /System/Library/Frameworks/AudioUnit.framework \
        /System/Library/Components/AudioCodecs.component \
        /System/Library/Components/AppleAACEncoder.component \
        /System/Library/Components/AppleAudioCodecs.component \
        /Library/Audio/Plug-Ins/Components/AudioCodecs.component; do
        for plist in "$bundle/Versions/A/Resources/Info.plist" "$bundle/Contents/Info.plist" "$bundle/Resources/Info.plist"; do
            if [ -f "$plist" ]; then
                echo "$bundle: $(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$plist" 2>&1) / $(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$plist" 2>&1)"
                break
            fi
        done
    done
    echo "--- component folders"
    ls /System/Library/Components /Library/Audio/Plug-Ins/Components 2>&1 | head -50
    echo "--- libraries afconvert loads"
    otool -L "$(command -v afconvert)" 2>&1 | head -30
    echo "--- afconvert --version, if it answers"
    afconvert --version 2>&1 | head -5
}
environment > "$OUT/environment.txt" 2>&1
say "== environment"; cat "$OUT/environment.txt"

# ---- what afconvert offers ---------------------------------------------------------------------
afconvert -h > "$OUT/afconvert-help.txt" 2>&1
echo "afconvert -h exit status $?" >> "$OUT/afconvert-help.txt"
afconvert -hf > "$OUT/afconvert-formats.txt" 2>&1
echo "afconvert -hf exit status $?" >> "$OUT/afconvert-formats.txt"
say "== afconvert -h"; cat "$OUT/afconvert-help.txt"
say "== afconvert -hf (the data formats of every file type)"; cat "$OUT/afconvert-formats.txt"

# A four-character ID is listed when it appears as a word in the -hf output.
listed() {
    grep -Eq "(^|[^A-Za-z0-9])$1([^A-Za-z0-9]|\$)" "$OUT/afconvert-formats.txt"
}
say "== IDs the -hf output names (every encode below is tried regardless, except where it says 'only if listed')"
for id in m4af m4bf mp4f 3gpp 3gp2 loas adts caff aac aach aacp aacl aace aacf aacg usac alac; do
    if listed "$id"; then say "$id: yes"; else say "$id: no"; fi
done | tee "$OUT/listed.txt"

VERBOSE=""
if grep -q -- '--verbose' "$OUT/afconvert-help.txt"; then VERBOSE="-v"; fi

# ---- the sources -------------------------------------------------------------------------------
python3 "$HERE/macos_aac_support.py" signals "$OUT" || { echo "could not write the source PCM" > "$OUT/problems.txt"; exit 1; }

# ---- one encode --------------------------------------------------------------------------------
REQUIRED=" lc-m4a-cbr-st44 he-m4a-cbr-st44 hev2-m4a-cbr-st44 lc-adts-cbr-st44 lc-caf-cbr-st44 alac-m4a-st44q16 "

extension() {
    case "$1" in
        m4af) echo m4a ;; m4bf) echo m4b ;; loas) echo loas ;; mp4f) echo mp4 ;; adts) echo aac ;; caff) echo caf ;; 3gpp) echo 3gp ;; 3gp2) echo 3g2 ;; *) echo bin ;;
    esac
}

# encode NAME SOURCE FILE-TYPE DATA-FORMAT STRATEGY-LABEL [afconvert option ...]
# DATA-FORMAT is the argument of -d, such as aac or aach@44100; the source's rate is kept without "@".
encode() {
    local name="$1" source="$2" filetype="$3" format="$4" label="$5"
    shift 5
    local ext input output log status required=0
    ext="$(extension "$filetype")"
    input="$(ls "$OUT/pcm/$source".* "$OUT/foreign/$source".* 2>/dev/null | head -1)"
    output="encoded/$name.$ext"
    log="logs/$name.log"
    case "$REQUIRED" in *" $name "*) required=1 ;; esac
    rm -f "${OUT:?}/${output:?}"
    # shellcheck disable=SC2206 # VERBOSE is empty or "-v"
    local command=(afconvert $VERBOSE -f "$filetype" -d "$format" "$@" "${input#"$OUT"/}" "$output")
    echo "${command[*]}" >> "$COMMANDS"
    say "-- $name: ${command[*]}"
    (
        cd "$OUT" || exit 99
        "${command[@]}" > "$log" 2>&1 &
        pid=$!
        ( sleep "$LIMIT"; kill "$pid" 2>/dev/null ) &
        watchdog=$!
        wait "$pid"
        status=$?
        kill "$watchdog" 2>/dev/null
        wait "$watchdog" 2>/dev/null
        exit "$status"
    )
    status=$?
    local flags="$*"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$source" "$filetype" "$format" "$label" "$flags" "$status" "$output" "${command[*]}" "$log" "$required" >> "$TSV"
    if [ "$status" -eq 0 ] && [ -s "$OUT/$output" ]; then
        say "   ok, $(wc -c < "$OUT/$output" | tr -d ' ') bytes"
        afinfo "$OUT/$output" > "$OUT/info/$name.afinfo.txt" 2>&1
    else
        say "   exit status $status, no file kept: $(tr '\n' ' ' < "$OUT/$log" | cut -c1-200)"
        rm -f "${OUT:?}/${output:?}"
    fi
}

# Strategy options, as afconvert -h documents them: -s 0 constant bitrate, 1 average bitrate, 2 constrained
# variable bitrate, 3 variable bitrate; -b the bitrate in bits per second; -u vbrq the variable bitrate
# quality (0 to 127); -q the codec quality (0 to 127). A rejected option is recorded and the run goes on;
# the log shows whether -h agrees with these.

# ---- AAC-LC ('aac') ----------------------------------------------------------------------------
say "== AAC-LC"
encode lc-m4a-cbr-st44       st44      m4af aac cbr -s 0 -b 128000
encode lc-m4a-cbr-mono44     mono44    m4af aac cbr -s 0 -b 64000
encode lc-m4a-cbr-st48       st48      m4af aac cbr -s 0 -b 128000
encode lc-m4a-cbr-st44-64    st44      m4af aac cbr -s 0 -b 64000
encode lc-m4a-cbr-st44-256   st44      m4af aac cbr -s 0 -b 256000
encode lc-m4a-default-st44   st44      m4af aac default
encode lc-m4a-abr-st44       st44      m4af aac abr -s 1 -b 128000
encode lc-m4a-cvbr-st44      st44      m4af aac constrained-vbr -s 2 -b 128000
encode lc-m4a-vbr-q64-st44   st44      m4af aac vbr -s 3 -u vbrq 64
encode lc-m4a-vbr-q127-st44  st44      m4af aac vbr -s 3 -u vbrq 127
encode lc-m4a-vbr-q0-st44    st44      m4af aac vbr -s 3 -u vbrq 0
encode lc-m4a-vbr-b128-st44  st44      m4af aac vbr -s 3 -b 128000
encode lc-m4a-vbr-q64-mono44 mono44    m4af aac vbr -s 3 -u vbrq 64
encode lc-m4a-q0-cbr-st44    st44      m4af aac cbr -s 0 -b 128000 -q 0
encode lc-m4a-q127-cbr-st44  st44      m4af aac cbr -s 0 -b 128000 -q 127
encode lc-m4a-tagged-st44    st44tag   m4af aac cbr -s 0 -b 128000
encode lc-adts-cbr-st44      st44      adts aac cbr -s 0 -b 128000
encode lc-adts-cbr-mono44    mono44    adts aac cbr -s 0 -b 64000
encode lc-adts-cbr-st48      st48      adts aac cbr -s 0 -b 128000
encode lc-adts-vbr-q64-st44  st44      adts aac vbr -s 3 -u vbrq 64
encode lc-caf-cbr-st44       st44      caff aac cbr -s 0 -b 128000
encode lc-caf-vbr-q64-st44   st44      caff aac vbr -s 3 -u vbrq 64
encode lc-mp4-cbr-st44       st44      mp4f aac cbr -s 0 -b 128000
encode lc-3gp-cbr-st44       st44      3gpp aac cbr -s 0 -b 128000
encode lc-3g2-cbr-st44       st44      3gp2 aac cbr -s 0 -b 128000

# ---- 5.1: the first run failed with "ExtAudioFileSetProperty ('cfmt') failed ('!dat')" for a WAV with a channel mask
# (WAVE_5_1_B) and for a CAF with MPEG_5_1_A. AAC's own order is MPEG_5_1_D, so the output layout is given, with the input
# layout too, and a CAF that carries MPEG_5_1_D already. The first variant that works carries on to ADTS, CAF and HE-AAC.
say "== AAC-LC 5.1"
SIX_SRC=""
SIX_OPTS=""
last_ok() { tail -1 "$TSV" | awk -F '\t' '{ exit ($7 != 0) }'; }
six_try() {
    # six_try NAME SOURCE option ...
    local name="$1" source="$2"
    shift 2
    encode "$name" "$source" m4af aac cbr -s 0 -b 384000 "$@"
    if [ -z "$SIX_SRC" ] && last_ok; then
        SIX_SRC="$source"
        SIX_OPTS="$*"
    fi
}
six_try lc-m4a-cbr-six48            six48
six_try lc-m4a-cbr-six48caf         six48caf
six_try lc-m4a-cbr-six48-lout       six48    -l MPEG_5_1_D
six_try lc-m4a-cbr-six48-lin-lout   six48    -l WAVE_5_1_B -l MPEG_5_1_D
six_try lc-m4a-cbr-six48caf-lio     six48caf -l MPEG_5_1_A -l MPEG_5_1_D
six_try lc-m4a-cbr-six48d           six48d
six_try lc-m4a-cbr-six48d-lout      six48d   -l MPEG_5_1_D
say "5.1 variant that worked: source '${SIX_SRC}' options '${SIX_OPTS}'"
if [ -n "$SIX_SRC" ]; then
    # shellcheck disable=SC2086 # SIX_OPTS is a list of options
    {
        encode lc-adts-cbr-six48-ok  "$SIX_SRC" adts aac  cbr -s 0 -b 384000 $SIX_OPTS
        encode lc-caf-cbr-six48-ok   "$SIX_SRC" caff aac  cbr -s 0 -b 384000 $SIX_OPTS
        encode he-m4a-cbr-six48-ok   "$SIX_SRC" m4af aach cbr -s 0 -b 128000 $SIX_OPTS
        encode he-adts-cbr-six48-ok  "$SIX_SRC" adts aach cbr -s 0 -b 128000 $SIX_OPTS
    }
fi

# ---- options afconvert -h documents that change what is written: no page alignment, Sound Check, hash, media kind,
# priming methods, 960-sample frames, 16-bit framing, the CAF user chunk
say "== AAC-LC options"
encode lc-m4a-nofiller-st44    st44 m4af aac cbr -s 0 -b 128000 --no-filler
encode lc-m4a-soundcheck-st44  st44 m4af aac cbr -s 0 -b 128000 --soundcheck-generate
encode lc-m4a-hash-st44        st44 m4af aac cbr -s 0 -b 128000 --generate-hash
encode lc-m4a-music-st44       st44 m4af aac cbr -s 0 -b 128000 --soundcheck-generate --media-kind Music
encode lc-m4a-podcast-st44     st44 m4af aac cbr -s 0 -b 128000 --soundcheck-generate --media-kind Podcast
encode lc-m4a-prime0-st44      st44 m4af aac cbr -s 0 -b 128000 --prime-method 0
encode lc-m4a-prime1-st44      st44 m4af aac cbr -s 0 -b 128000 --prime-method 1
encode lc-m4a-prime2-st44      st44 m4af aac cbr -s 0 -b 128000 --prime-method 2
encode lc-m4a-960-st44         st44 m4af "aac#960" cbr -s 0 -b 128000
encode lc-m4a-condensed-st44   st44 m4af aac cbr -s 0 -b 128000 --condensed-framing 16
encode lc-m4b-cbr-st44         st44 m4bf aac cbr -s 0 -b 128000 --soundcheck-generate --media-kind Audiobook
encode lc-caf-tag-st44         st44 caff aac cbr -s 0 -b 128000 -t

# ---- HE-AAC ('aach') ---------------------------------------------------------------------------
say "== HE-AAC"
encode he-m4a-cbr-st44       st44h      m4af aach cbr -s 0 -b 64000
encode he-m4a-cbr-st44-32    st44h      m4af aach cbr -s 0 -b 32000
encode he-m4a-cbr-mono44     mono44h    m4af aach cbr -s 0 -b 32000
encode he-m4a-cbr-st48       st48h      m4af aach cbr -s 0 -b 48000
encode he-m4a-cbr-st22       st22      m4af aach cbr -s 0 -b 24000
encode he-m4a-cbr-mono22     mono22    m4af aach cbr -s 0 -b 16000
encode he-m4a-abr-st44       st44h      m4af aach abr -s 1 -b 64000
encode he-m4a-vbr-q64-st44   st44h      m4af aach vbr -s 3 -u vbrq 64
encode he-adts-cbr-st44      st44h      adts aach cbr -s 0 -b 64000
encode he-adts-cbr-mono44    mono44h    adts aach cbr -s 0 -b 32000
encode he-caf-cbr-st44       st44h      caff aach cbr -s 0 -b 64000

# ---- HE-AAC v2 ('aacp'): parametric stereo, so stereo input -------------------------------------
say "== HE-AAC v2"
encode hev2-m4a-cbr-st44     st44h      m4af aacp cbr -s 0 -b 32000
encode hev2-m4a-cbr-st44-24  st44h      m4af aacp cbr -s 0 -b 24000
encode hev2-m4a-cbr-st44-48  st44h      m4af aacp cbr -s 0 -b 48000
encode hev2-m4a-cbr-st48     st48h      m4af aacp cbr -s 0 -b 32000
encode hev2-m4a-cbr-st22     st22      m4af aacp cbr -s 0 -b 24000
encode hev2-m4a-abr-st44     st44h      m4af aacp abr -s 1 -b 32000
encode hev2-m4a-vbr-q64-st44 st44h      m4af aacp vbr -s 3 -u vbrq 64
encode hev2-m4a-cbr-mono44   mono44h    m4af aacp cbr -s 0 -b 32000
encode hev2-adts-cbr-st44    st44h      adts aacp cbr -s 0 -b 32000
encode hev2-caf-cbr-st44     st44h      caff aacp cbr -s 0 -b 32000

# ---- the other AAC formats, only where -hf lists the ID -------------------------------------------
say "== AAC-LD, AAC-ELD, ELD with SBR (only if listed)"
for id in aacl aace aacf aacg; do
    if listed "$id"; then
        encode "$id-m4a-cbr-st44"     st44   m4af "$id" cbr -s 0 -b 64000
        encode "$id-caf-cbr-st44"     st44   caff "$id" cbr -s 0 -b 64000
        encode "$id-m4a-cbr-mono44"   mono44 m4af "$id" cbr -s 0 -b 32000
        encode "$id-m4a-vbr-q64-st44" st44   m4af "$id" vbr -s 3 -u vbrq 64
    else
        say "-- $id is not listed by afconvert -hf; not tried"
    fi
done

# ---- xHE-AAC: -hf lists usac for caff, loas and mp4f only, and the first run's CAF encode failed with
# "ExtAudioFileSetProperty ('cfmt') failed ('fmt?')", the client format being refused. Tried over the file types
# that list it, 44.1 and 48 kHz, 16-bit integer and 32-bit float sources, mono and stereo, with and without a bitrate.
if listed usac; then
    say "== xHE-AAC (usac)"
    for ft in mp4f caff loas; do
        for src in st44 st48 st48f32 mono48f32; do
            encode "usac-$ft-$src"        "$src" "$ft" usac default
            encode "usac-$ft-$src-b64"    "$src" "$ft" usac cbr -b 64000
            encode "usac-$ft-$src-vbr64"  "$src" "$ft" usac vbr -s 3 -u vbrq 64
        done
    done
fi

# ---- Apple Lossless ('alac') for phase 3: the bit depth is the source's ---------------------------
say "== ALAC"
encode alac-m4a-st44q16      st44q16   m4af alac lossless
encode alac-caf-st44q16      st44q16   caff alac lossless
encode alac-m4a-st44q24      st44q24   m4af alac lossless
encode alac-caf-st44q24      st44q24   caff alac lossless
encode alac-m4a-mono44       mono44    m4af alac lossless
encode alac-m4a-six48q16     six48q16  m4af alac lossless
encode alac-caf-six48q16     six48q16  caff alac lossless

# ---- packets of other encoders, copied: -d 0 "specifies the same format as the source file, with packets copied
# exactly". The sources are fixtures of this repository that Apple's encoder did not write (ffmpeg, FAAC and fdkaac,
# in ADTS and M4A), copied into foreign/ by macos_aac_support.py. Does Apple's file writer then add the traces its
# own encodes have (stream type byte 0x14, the sbtd box, the padding to 4,096), and does an ADTS file get its headers?
say "== packets copied from other encoders (-d 0)"
encode passthru-m4a-from-ffmpeg-adts   ffmpeg-adts     m4af 0 copy
encode passthru-adts-from-ffmpeg-adts   ffmpeg-adts     adts 0 copy
encode passthru-caf-from-ffmpeg-adts    ffmpeg-adts     caff 0 copy
encode passthru-m4a-from-ffmpeg-m4a     ffmpeg-m4a      m4af 0 copy
encode passthru-adts-from-ffmpeg-m4a    ffmpeg-m4a      adts 0 copy
encode passthru-m4a-from-faac-m4a       faac-m4a        m4af 0 copy
encode passthru-mp4-from-faac-m4a       faac-m4a        mp4f 0 copy
encode passthru-adts-from-faac-m4a      faac-m4a        adts 0 copy
encode passthru-m4a-from-fdkaac-m4a     fdkaac-m4a      m4af 0 copy
encode passthru-adts-from-fdkaac-m4a    fdkaac-m4a      adts 0 copy
encode passthru-m4a-from-fdkaac-he-m4a  fdkaac-he-m4a   m4af 0 copy
encode passthru-m4a-from-fdkaac-he-adts fdkaac-he-adts  m4af 0 copy
encode passthru-adts-from-fdkaac-he-adts fdkaac-he-adts adts 0 copy

# ---- the window shape matrix: does Apple's AAC encoder use sine windows only? -----------------------------------------
# The 8 s sources win-* (window_signals.py): speech, music with sharp attacks, noise, tones, sweeps, transients, wide and
# narrow stereo, mono. Names read win-<codec>-<file type>-<strategy and bitrate>-<source>. After the run, scripts/fixtures/
# window_shapes.py <artifact>/encoded tabulates the share of sine and KBD windows per file and window sequence.
# Per stereo source: AAC-LC at 64, 128 and 256 kbps (constant), variable quality 0, 64 and 127, constrained variable 128,
# -q 0 and -q 127 at 128 kbps constant (q127 is the setting that gave KBD windows before), HE-AAC at 64 and 96, HE-AAC v2 at 32
# and 48. Per mono source: AAC-LC at 64, 128 and 192, the same variable and -q runs, HE-AAC at 32 and 48. -q 127 also at the
# lowest and highest constant bitrate for some, and ADTS for a few. M4A otherwise.
say "== window shape matrix"
WIN_STEREO="pluck clicks chords white pink tones sweep transient wide narrow"
WIN_MONO="speech mix"
win_lc() {
    # win_lc SOURCE BITRATE-LOW BITRATE-MID BITRATE-HIGH
    local s="$1" lo="$2" mid="$3" hi="$4"
    encode "win-lc-m4a-cbr$lo-$s"      "win-$s" m4af aac cbr -s 0 -b "${lo}000"
    encode "win-lc-m4a-cbr$mid-$s"     "win-$s" m4af aac cbr -s 0 -b "${mid}000"
    encode "win-lc-m4a-cbr$hi-$s"      "win-$s" m4af aac cbr -s 0 -b "${hi}000"
    encode "win-lc-m4a-vbrq0-$s"       "win-$s" m4af aac vbr -s 3 -u vbrq 0
    encode "win-lc-m4a-vbrq64-$s"      "win-$s" m4af aac vbr -s 3 -u vbrq 64
    encode "win-lc-m4a-vbrq127-$s"     "win-$s" m4af aac vbr -s 3 -u vbrq 127
    encode "win-lc-m4a-cvbr$mid-$s"    "win-$s" m4af aac constrained-vbr -s 2 -b "${mid}000"
    encode "win-lc-m4a-q0-cbr$mid-$s"   "win-$s" m4af aac cbr -s 0 -b "${mid}000" -q 0
    encode "win-lc-m4a-q127-cbr$mid-$s" "win-$s" m4af aac cbr -s 0 -b "${mid}000" -q 127
}
for s in $WIN_STEREO; do
    win_lc "$s" 64 128 256
    encode "win-he-m4a-cbr64-$s"    "win-$s" m4af aach cbr -s 0 -b 64000
    encode "win-he-m4a-cbr96-$s"    "win-$s" m4af aach cbr -s 0 -b 96000
    encode "win-hev2-m4a-cbr32-$s"  "win-$s" m4af aacp cbr -s 0 -b 32000
    encode "win-hev2-m4a-cbr48-$s"  "win-$s" m4af aacp cbr -s 0 -b 48000
done
for s in $WIN_MONO; do
    win_lc "$s" 64 128 192
    encode "win-he-m4a-cbr32-$s"    "win-$s" m4af aach cbr -s 0 -b 32000
    encode "win-he-m4a-cbr48-$s"    "win-$s" m4af aach cbr -s 0 -b 48000
done
for s in pluck clicks white transient; do
    encode "win-lc-m4a-q127-cbr64-$s"  "win-$s" m4af aac cbr -s 0 -b 64000 -q 127
    encode "win-lc-m4a-q127-cbr256-$s" "win-$s" m4af aac cbr -s 0 -b 256000 -q 127
done
encode "win-lc-m4a-q127-cbr64-mix"  "win-mix" m4af aac cbr -s 0 -b 64000 -q 127
encode "win-lc-m4a-q127-cbr192-mix" "win-mix" m4af aac cbr -s 0 -b 192000 -q 127
for s in speech clicks white transient; do
    encode "win-lc-adts-cbr128-$s"      "win-$s" adts aac cbr -s 0 -b 128000
    encode "win-lc-adts-q127-cbr128-$s" "win-$s" adts aac cbr -s 0 -b 128000 -q 127
done
encode "win-he-adts-cbr64-clicks" "win-clicks" adts aach cbr -s 0 -b 64000

# ---- record ------------------------------------------------------------------------------------
say "== manifest"
python3 "$HERE/macos_aac_support.py" manifest "$OUT" "$TSV" "$OUT/environment.txt"

# The run fails when a required encode has no file.
: > "$OUT/problems.txt"
for name in $REQUIRED; do
    if ! awk -F '\t' -v n="$name" '$1 == n && $7 == 0 { found = 1 } END { exit !found }' "$TSV" || [ -z "$(ls "$OUT"/encoded/"$name".* 2>/dev/null)" ]; then
        echo "required encode $name produced no file; see logs/$name.log" >> "$OUT/problems.txt"
    fi
done
total=$(wc -l < "$TSV" | tr -d ' ')
made=$(ls "$OUT/encoded" | wc -l | tr -d ' ')
say "== $made of $total encodes produced a file"
if [ -s "$OUT/problems.txt" ]; then
    cat "$OUT/problems.txt"
    exit 1
fi
rm -f "${OUT:?}/problems.txt"
exit 0

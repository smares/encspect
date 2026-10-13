# Encodes the project's WMA fixtures with Microsoft's encoders, through the Windows Media Format
# SDK's writer in wmvcore.dll (WmfWriter.cs): WMA 9.2 standard, WMA 10 Professional, WMA 9.2
# Lossless and WMA Voice 9, constant and variable bitrate, mono, stereo and 5.1 where the SDK
# offers them. Runs under Windows PowerShell 5.1 (powershell.exe), which the Windows runners have,
# and needs Python 3 for the test signals (wma_signals.py).
#
#   powershell -File scripts/fixtures/windows/make_windows_wma_fixtures.ps1 -Out wma-out
#
# Writes into -Out:
#   wma/<name>.wma      the encoded files
#   pcm/<name>.wav      the PCM each was encoded from
#   formats.txt         every format of every codec, as the SDK lists them, in both modes
#   encodes.json        one entry per encode: codec, format, mode, settings, input, size, SHA-256,
#                       and why an encode failed
#   environment.json    Windows version, wmvcore.dll version, runner image
#   log.txt             everything the script printed
#
# An encode that fails is logged and skipped; the others still run, so one run shows what the
# SDK offers even where the plan below asks for more than it has.
param(
    [string]$Out = (Join-Path (Get-Location) 'wma-out'),
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$signals = Join-Path (Split-Path -Parent $here) 'wma_signals.py'
New-Item -ItemType Directory -Force -Path (Join-Path $Out 'wma'), (Join-Path $Out 'pcm') | Out-Null
Start-Transcript -Path (Join-Path $Out 'log.txt') -Force | Out-Null

Add-Type -TypeDefinition (Get-Content -Raw (Join-Path $here 'WmfWriter.cs'))

function Write-Json($value, $path) {
    $text = $value | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($path, $text + "`n", (New-Object Text.UTF8Encoding $false))
}

Write-Host '=== environment ==='
$os = Get-CimInstance Win32_OperatingSystem
$wmvcore = (Get-Item (Join-Path $env:windir 'System32\wmvcore.dll')).VersionInfo
$environment = [ordered]@{
    windowsCaption   = $os.Caption
    windowsVersion   = $os.Version
    windowsBuild     = $os.BuildNumber
    architecture     = $env:PROCESSOR_ARCHITECTURE
    runnerImageOS    = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    runnerOS         = $env:RUNNER_OS
    runnerArch       = $env:RUNNER_ARCH
    powershell       = $PSVersionTable.PSVersion.ToString()
    wmvcoreVersion   = $wmvcore.FileVersion
    wmvcoreProduct   = $wmvcore.ProductVersion
    wmvcoreDescription = $wmvcore.FileDescription
}
$environment.GetEnumerator() | ForEach-Object { Write-Host ('{0}: {1}' -f $_.Key, $_.Value) }
Write-Json $environment (Join-Path $Out 'environment.json')

Write-Host '=== codecs ==='
$codecs = [Encspect.Wmf]::CodecNames()
$codecs | ForEach-Object { Write-Host $_ }

Write-Host '=== formats ==='
$formats = New-Object Text.StringBuilder
foreach ($codec in $codecs) {
    foreach ($vbr in @($false, $true)) {
        try { [void]$formats.Append([Encspect.Wmf]::ListFormats($codec, $vbr)) }
        catch { [void]$formats.AppendLine("$codec, vbr=${vbr}: listing failed: $($_.Exception.GetBaseException().Message)") }
    }
}
[IO.File]::WriteAllText((Join-Path $Out 'formats.txt'), $formats.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host $formats.ToString()

$standard = 'Windows Media Audio 9.2'
$pro = 'Windows Media Audio 10 Professional'
$lossless = 'Windows Media Audio 9.2 Lossless'
$voice = 'Windows Media Audio Voice 9'

# Constant bitrate: the Windows Media Format SDK writer lists only variable bitrate formats for the
# standard, Professional and Lossless codecs on these runners (the first two runs: _VBRENABLED=0 changes
# nothing and _PASSESUSED fails), so the constant bitrate encodes of WMA 9.2 and Professional (Path =
# 'mf') go through the Media Foundation transcoder (MediaEncodingProfile.CreateWma, Windows.Media
# .Transcoding) with the profile's Audio.Bitrate. Everything else is written by the SDK writer. Each
# record says which path wrote the file (encoderPath).
# Optional = $true marks an encode the SDK may not offer (mono, 22 kHz, Voice): it is recorded as
# unavailable with the reason and does not fail the run. Every other encode must succeed, in the mode
# asked for, and no two encodes may write the same file; the run fails otherwise.
# Name, codec, mode, channels, preferred sample rates in order, quality (variable bitrate) or
# bitrate in bits per second (constant), seconds and signal. The format is the SDK's closest
# match, and the encode log says which one it was.
$plan = @(
    @{ Name = 'wma92-cbr-stereo-44k-64'; Path = 'mf';   Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 64000;  Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-44k-128'; Path = 'mf';  Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-44k-192'; Path = 'mf';  Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 192000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-48k-128'; Path = 'mf';  Codec = $standard; Vbr = $false; Channels = 2; Rates = @(48000); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-22k-32'; Path = 'mf'; Optional = $true;   Codec = $standard; Vbr = $false; Channels = 2; Rates = @(22050); Bitrate = 32000;  Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-mono-44k-48'; Path = 'mf'; Optional = $true;     Codec = $standard; Vbr = $false; Channels = 1; Rates = @(44100, 22050); Bitrate = 48000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q25';  Codec = $standard; Vbr = $true;  Channels = 2; Rates = @(44100); Quality = 25; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q75';  Codec = $standard; Vbr = $true;  Channels = 2; Rates = @(44100); Quality = 75; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q90';  Codec = $standard; Vbr = $true;  Channels = 2; Rates = @(44100); Quality = 90; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-mono-44k-q50'; Optional = $true;    Codec = $standard; Vbr = $true;  Channels = 1; Rates = @(44100, 22050); Quality = 50; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-128'; Path = 'mf'; Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-192'; Path = 'mf'; Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 192000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-384'; Path = 'mf'; Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 384000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-stereo-44k-q10'; Codec = $pro; Vbr = $true; Channels = 2; Rates = @(44100); Quality = 10; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-stereo-44k-q50'; Codec = $pro; Vbr = $true; Channels = 2; Rates = @(44100); Quality = 50; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-stereo-44k-q98'; Codec = $pro; Vbr = $true; Channels = 2; Rates = @(44100); Quality = 98; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-mono-44k-64'; Path = 'mf'; Optional = $true;    Codec = $pro; Vbr = $false; Channels = 1; Rates = @(44100, 48000); Bitrate = 64000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-5.1-48k-384'; Path = 'mf';    Codec = $pro; Vbr = $false; Channels = 6; Rates = @(48000, 44100); Bitrate = 384000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-5.1-48k-q50';    Codec = $pro; Vbr = $true;  Channels = 6; Rates = @(48000, 44100); Quality = 50; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92lossless-stereo-44k-16'; Codec = $lossless; Vbr = $true; Channels = 2; Rates = @(44100); Bits = 16; Quality = 100; Seconds = 2; Signal = 'quiet' }
    @{ Name = 'wma92lossless-stereo-44k-24'; Codec = $lossless; Vbr = $true; Channels = 2; Rates = @(44100); Bits = 24; Quality = 100; Seconds = 2; Signal = 'quiet' }
    @{ Name = 'wma92lossless-mono-44k-16'; Optional = $true;   Codec = $lossless; Vbr = $true; Channels = 1; Rates = @(44100); Bits = 16; Quality = 100; Seconds = 2; Signal = 'quiet' }
    @{ Name = 'wma92lossless-5.1-48k-24';    Codec = $lossless; Vbr = $true; Channels = 6; Rates = @(48000, 44100); Bits = 24; Quality = 100; Seconds = 0.5; Signal = 'quiet' }
    @{ Name = 'wmavoice-cbr-mono-8k-5'; Optional = $true;      Codec = $voice; Vbr = $false; Channels = 1; Rates = @(8000, 16000); Bitrate = 5000;  Seconds = 3; Signal = 'tones' }
    @{ Name = 'wmavoice-cbr-mono-16k-20'; Optional = $true;    Codec = $voice; Vbr = $false; Channels = 1; Rates = @(16000, 22050); Bitrate = 20000; Seconds = 3; Signal = 'tones' }
    # Constant bitrate through the SDK's writer, which differs in its header from Media Foundation's; optional,
    # since whether the SDK lists constant bitrate formats for these codecs is what the run finds out.
    @{ Name = 'wma92-cbr-stereo-44k-128-sdk'; Optional = $true;     Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-128-sdk'; Optional = $true;  Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-384-sdk'; Optional = $true;  Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 384000; Seconds = 3; Signal = 'tones' }
    # Low rates and low quality make the encoder code noise bands (the high-band gain codes of the
    # standard decoder); the files are checked for them by the analyzer's tests.
    @{ Name = 'wma92-cbr-stereo-44k-48-sdk'; Optional = $true;      Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 48000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-44k-32-sdk'; Optional = $true;      Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 32000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-22k-20-sdk'; Optional = $true;      Codec = $standard; Vbr = $false; Channels = 2; Rates = @(22050); Bitrate = 20000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q10'; Optional = $true;         Codec = $standard; Vbr = $true;  Channels = 2; Rates = @(44100); Quality = 10; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q75-tagged'; Codec = $standard; Vbr = $true; Channels = 2; Rates = @(44100); Quality = 75; Seconds = 2; Signal = 'tones'; Title = 'Encspect test tone'; Author = 'Encspect' }
)

function New-Signal($entry, $channels, $rate, $bits, $path) {
    & $Python $signals $path --channels $channels --rate $rate --bits $bits --seconds $entry.Seconds --kind $entry.Signal
    if ($LASTEXITCODE -ne 0) { throw "$Python $signals failed with exit code $LASTEXITCODE" }
}


# ---- Media Foundation (Windows.Media.Transcoding) ----
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 }
$asOperation = $asTask | Where-Object { $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asProgress = $asTask | Where-Object { $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncActionWithProgress`1' } | Select-Object -First 1
function Await($operation, [type]$type) { $t = $asOperation.MakeGenericMethod($type).Invoke($null, @($operation)); $t.Wait(-1) | Out-Null; $t.Result }
function AwaitProgress($action) { $t = $asProgress.MakeGenericMethod([double]).Invoke($null, @($action)); $t.Wait(-1) | Out-Null }
[void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
[void][Windows.Storage.StorageFolder, Windows.Storage, ContentType = WindowsRuntime]
[void][Windows.Media.Transcoding.MediaTranscoder, Windows.Media.Transcoding, ContentType = WindowsRuntime]
[void][Windows.Media.MediaProperties.MediaEncodingProfile, Windows.Media.MediaProperties, ContentType = WindowsRuntime]

# The groups of two or more results that wrote the same file (by SHA-256). The results are ordered
# dictionaries, which Group-Object cannot read by key, so they are grouped by hand.
function Find-Duplicates($results) {
    $byHash = @{}
    foreach ($r in $results) {
        if ($r['status'] -ne 'ok' -or -not $r['sha256']) { continue }
        if (-not $byHash.ContainsKey($r['sha256'])) { $byHash[$r['sha256']] = New-Object System.Collections.ArrayList }
        [void]$byHash[$r['sha256']].Add($r['name'])
    }
    $groups = @()
    foreach ($key in $byHash.Keys) { if ($byHash[$key].Count -gt 1) { $groups += ,($byHash[$key] -join ', ') } }
    return $groups
}

# Run on every start: the check above must find a real duplicate and not report distinct files.
$sample = @(
    [ordered]@{ name = 'a'; status = 'ok'; sha256 = '11' }, [ordered]@{ name = 'b'; status = 'ok'; sha256 = '22' },
    [ordered]@{ name = 'c'; status = 'ok'; sha256 = '11' }, [ordered]@{ name = 'd'; status = 'failed'; sha256 = '11' }
)
$found = @(Find-Duplicates $sample)
if ($found.Count -ne 1 -or $found[0] -ne 'a, c') { throw "Find-Duplicates is wrong: $($found -join '; ')" }
if (@(Find-Duplicates $sample[0..1]).Count -ne 0) { throw 'Find-Duplicates reports distinct files' }

function Get-AsfAudio($bytes) {
    $guid = [byte[]](0x40,0x9E,0x69,0xF8,0x4D,0x5B,0xCF,0x11,0xA8,0xFD,0x00,0x80,0x5F,0x5C,0x44,0x2B)
    for ($i = 0; $i -lt $bytes.Length - 80; $i++) {
        $match = $true
        for ($j = 0; $j -lt 16; $j++) { if ($bytes[$i + $j] -ne $guid[$j]) { $match = $false; break } }
        if ($match) {
            return [ordered]@{
                tag = [BitConverter]::ToUInt16($bytes, $i + 54); channels = [BitConverter]::ToUInt16($bytes, $i + 56)
                rate = [BitConverter]::ToUInt32($bytes, $i + 58); bytesPerSecond = [BitConverter]::ToUInt32($bytes, $i + 62)
                blockAlign = [BitConverter]::ToUInt16($bytes, $i + 66); bits = [BitConverter]::ToUInt16($bytes, $i + 68)
            }
        }
    }
    return $null
}

# One constant bitrate encode through the transcoder; fills and returns the record.
function Invoke-MediaFoundation($entry, $record) {
    $root = (Resolve-Path $Out).Path
    $rate = [int]$entry.Rates[0]
    $wav = Join-Path $root "pcm\$($entry.Name).wav"
    $wma = Join-Path $root "wma\$($entry.Name).wma"
    New-Signal $entry $entry.Channels $rate 16 $wav
    $folder = Await ([Windows.Storage.StorageFolder]::GetFolderFromPathAsync((Join-Path $root 'wma'))) ([Windows.Storage.StorageFolder])
    $source = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($wav)) ([Windows.Storage.StorageFile])
    $target = Await ($folder.CreateFileAsync("$($entry.Name).wma", [Windows.Storage.CreationCollisionOption]::ReplaceExisting)) ([Windows.Storage.StorageFile])
    $encProfile = [Windows.Media.MediaProperties.MediaEncodingProfile]::CreateWma([Windows.Media.MediaProperties.AudioEncodingQuality]::High)
    $subtype = if ($entry.Codec -eq $pro) { 'WMA9' } else { 'WMA8' }
    $encProfile.Audio.Subtype = $subtype
    $encProfile.Audio.Bitrate = [uint32]$entry.Bitrate
    $encProfile.Audio.SampleRate = [uint32]$rate
    $encProfile.Audio.ChannelCount = [uint32]$entry.Channels
    Write-Host ("profile: {0}, {1} bps, {2} Hz, {3} channels, {4} bits" -f $subtype, $encProfile.Audio.Bitrate, $encProfile.Audio.SampleRate, $encProfile.Audio.ChannelCount, $encProfile.Audio.BitsPerSample)
    $record.encoderPath = 'media-foundation'
    $record.formatDescription = "Media Foundation $subtype at $($entry.Bitrate) bps"
    $prepared = Await ((New-Object Windows.Media.Transcoding.MediaTranscoder).PrepareFileTranscodeAsync($source, $target, $encProfile)) ([Windows.Media.Transcoding.PrepareTranscodeResult])
    if (-not $prepared.CanTranscode) { $record.status = 'cannot transcode'; $record.log = "$($prepared.FailureReason)"; return $record }
    AwaitProgress ($prepared.TranscodeAsync())
    $bytes = [IO.File]::ReadAllBytes($wma)
    $audio = Get-AsfAudio $bytes
    if (-not $audio) { $record.status = 'no audio stream'; return $record }
    $achieved = [int]($bytes.Length * 8 / $entry.Seconds)
    $record.codecName = "Windows Media Audio ($subtype, Media Foundation)"
    $record.formatTag = '0x{0:X4}' -f $audio.tag
    $record.formatChannels = $audio.channels
    $record.formatRate = $audio.rate
    $record.formatBits = $audio.bits
    $record.formatBitrate = [int]$entry.Bitrate
    $record.achievedBitrate = $achieved
    $record.audioBytesPerSecond = $audio.bytesPerSecond
    $record.blockAlign = $audio.blockAlign
    $record.inputBits = 16
    $record.inputType = 'plain PCM WAV'
    $record.frames = [int]($rate * $entry.Seconds)
    $expectedTag = if ($entry.Codec -eq $pro) { 0x162 } else { 0x161 }
    if ($audio.tag -ne $expectedTag) { $record.status = 'wrong codec'; $record.log = ('format tag 0x{0:X4}' -f $audio.tag); return $record }
    # A constant bitrate file is close to the bitrate asked for.
    if ($achieved -lt 0.5 * $entry.Bitrate -or $achieved -gt 1.5 * $entry.Bitrate) { $record.status = 'wrong bitrate'; $record.log = "$achieved bps for $($entry.Bitrate)"; return $record }
    $record.status = 'ok'
    $record.file = "wma/$($entry.Name).wma"
    $record.pcm = "pcm/$($entry.Name).wav"
    $record.bytes = $bytes.Length
    $record.sha256 = (Get-FileHash -Algorithm SHA256 $wma).Hash.ToLowerInvariant()
    return $record
}

$results = @()
foreach ($entry in $plan) {
    Write-Host "=== $($entry.Name) ==="
    $request = New-Object Encspect.EncodeRequest
    $request.Codec = $entry.Codec
    $request.Vbr = [bool]$entry.Vbr
    $request.Channels = [int]$entry.Channels
    $request.Rates = [int[]]$entry.Rates
    $request.Bits = if ($entry.Bits) { [int]$entry.Bits } else { 0 }
    $request.Quality = if ($entry.Quality) { [int]$entry.Quality } else { 0 }
    $request.Bitrate = if ($entry.Bitrate) { [int]$entry.Bitrate } else { 0 }
    $request.Title = $entry.Title
    $request.Author = $entry.Author
    $record = [ordered]@{
        name = $entry.Name; codec = $entry.Codec; mode = $(if ($entry.Vbr) { 'vbr' } else { 'cbr' })
        requestedChannels = $entry.Channels; requestedRates = $entry.Rates; requestedFormatBits = $entry.Bits
        requestedQuality = $entry.Quality; requestedBitrate = $entry.Bitrate
        title = $entry.Title; author = $entry.Author; seconds = $entry.Seconds; signal = $entry.Signal
        status = 'failed'; optional = [bool]$entry.Optional; encoderPath = 'wmf-sdk'
    }
    try {
        if ($entry.Path -eq 'mf') {
            $record.formatIndex = $null
            $record = Invoke-MediaFoundation $entry $record
            Write-Host "$($record.name): $($record.status) $($record.log)"
            $results += $record
            continue
        }
        $preview = [Encspect.Wmf]::Preview($request)
        Write-Host $preview.Log
        if (-not $preview.FormatDescription) { $record.log = $preview.Log; $record.status = 'no format'; $results += $record; continue }
        $variable = $preview.FormatDescription -match 'VBR'
        if ($variable -ne [bool]$entry.Vbr) { $record.log = $preview.Log; $record.status = 'wrong mode'; $record.formatDescription = $preview.FormatDescription; $results += $record; continue }

        # The PCM has the format's channels and rate, and its bits (24 for a format of more than
        # 16); a refusal of 24-bit input is retried with 16.
        $inputBits = if ($preview.FormatBits -gt 16) { 24 } else { 16 }
        $wma = Join-Path $Out "wma\$($entry.Name).wma"
        $encoded = $null
        foreach ($bits in @($inputBits, 16) | Select-Object -Unique) {
            $wav = Join-Path $Out "pcm\$($entry.Name).wav"
            New-Signal $entry $preview.Channels $preview.Rate $bits $wav
            $request.InputPath = $wav
            $request.OutputPath = $wma
            if (Test-Path $wma) { Remove-Item $wma }
            $encoded = [Encspect.Wmf]::Encode($request)
            Write-Host $encoded.Log
            if ($encoded.Bytes -gt 0) { break }
        }
        $record.codecName = $preview.CodecName
        $record.formatIndex = $preview.FormatIndex
        $record.formatDescription = $preview.FormatDescription
        $record.formatTag = '0x{0:X4}' -f $preview.FormatTag
        $record.formatChannels = $preview.Channels
        $record.formatRate = $preview.Rate
        $record.formatBits = $preview.FormatBits
        $record.formatBitrate = $preview.Bitrate
        $record.configBitrate = $encoded.ConfigBitrate
        $record.bufferWindow = $encoded.BufferWindow
        $record.inputBits = $encoded.InputBits
        $record.inputType = $encoded.InputType
        $record.frames = $encoded.Frames
        $record.log = $encoded.Log
        if ($entry.Title) { $record.tagsVerified = [bool]$encoded.TagsVerified }
        if ($encoded.Bytes -gt 0) {
            $record.status = 'ok'
            $record.file = "wma/$($entry.Name).wma"
            $record.pcm = "pcm/$($entry.Name).wav"
            $record.bytes = $encoded.Bytes
            $record.sha256 = (Get-FileHash -Algorithm SHA256 $wma).Hash.ToLowerInvariant()
            if ($entry.Title -and -not $encoded.TagsVerified) { $record.status = 'tags differ' }
        }
    }
    catch {
        $record.log = "$($_.Exception.GetBaseException().Message)"
        Write-Host "failed: $($record.log)"
    }
    $results += $record
}

Write-Json $results (Join-Path $Out 'encodes.json')
Write-Host '=== summary ==='
foreach ($r in $results) {
    Write-Host ('{0,-34} {1,-10} {2}' -f $r.name, $r.status, $r.formatDescription)
}

# The run fails where it would otherwise pass silently with files that are not what was asked for.
$problems = @()
foreach ($r in $results) {
    if ($r.status -ne 'ok' -and -not $r.optional) { $problems += "$($r.name): $($r.status)" }
    if ($r.log -match 'MISMATCH') { $problems += "$($r.name): a setting did not read back as it was set" }
    if ($r.status -eq 'wrong mode' -and -not $r.optional) { $problems += "$($r.name): the SDK offered only $($r.formatDescription) for a $($r.mode) request" }
}
foreach ($g in @(Find-Duplicates $results)) { $problems += "identical output: $g" }
Stop-Transcript | Out-Null
if ($problems.Count -gt 0) {
    Write-Host '=== problems ==='
    $problems | ForEach-Object { Write-Host $_ }
    [IO.File]::WriteAllLines((Join-Path $Out 'problems.txt'), [string[]]$problems)
    exit 1
}

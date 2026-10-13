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

# Optional = $true marks an encode the SDK may not offer (mono, 22 kHz, Voice): it is recorded as
# unavailable with the reason and does not fail the run. Every other encode must succeed, in the mode
# asked for, and no two encodes may write the same file; the run fails otherwise.
# Name, codec, mode, channels, preferred sample rates in order, quality (variable bitrate) or
# bitrate in bits per second (constant), seconds and signal. The format is the SDK's closest
# match, and the encode log says which one it was.
$plan = @(
    @{ Name = 'wma92-cbr-stereo-44k-64';   Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 64000;  Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-44k-128';  Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-44k-192';  Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 192000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-48k-128';  Codec = $standard; Vbr = $false; Channels = 2; Rates = @(48000); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-22k-32'; Optional = $true;   Codec = $standard; Vbr = $false; Channels = 2; Rates = @(22050); Bitrate = 32000;  Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-mono-44k-48'; Optional = $true;     Codec = $standard; Vbr = $false; Channels = 1; Rates = @(44100, 22050); Bitrate = 48000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q25';  Codec = $standard; Vbr = $true;  Channels = 2; Rates = @(44100); Quality = 25; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q75';  Codec = $standard; Vbr = $true;  Channels = 2; Rates = @(44100); Quality = 75; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-stereo-44k-q90';  Codec = $standard; Vbr = $true;  Channels = 2; Rates = @(44100); Quality = 90; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-vbr-mono-44k-q50'; Optional = $true;    Codec = $standard; Vbr = $true;  Channels = 1; Rates = @(44100, 22050); Quality = 50; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-128'; Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 128000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-192'; Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 192000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-stereo-44k-384'; Codec = $pro; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 384000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-stereo-44k-q10'; Codec = $pro; Vbr = $true; Channels = 2; Rates = @(44100); Quality = 10; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-stereo-44k-q50'; Codec = $pro; Vbr = $true; Channels = 2; Rates = @(44100); Quality = 50; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-stereo-44k-q98'; Codec = $pro; Vbr = $true; Channels = 2; Rates = @(44100); Quality = 98; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-mono-44k-64'; Optional = $true;    Codec = $pro; Vbr = $false; Channels = 1; Rates = @(44100, 48000); Bitrate = 64000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-cbr-5.1-48k-384';    Codec = $pro; Vbr = $false; Channels = 6; Rates = @(48000, 44100); Bitrate = 384000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma10pro-vbr-5.1-48k-q50';    Codec = $pro; Vbr = $true;  Channels = 6; Rates = @(48000, 44100); Quality = 50; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92lossless-stereo-44k-16'; Codec = $lossless; Vbr = $true; Channels = 2; Rates = @(44100); Bits = 16; Quality = 100; Seconds = 2; Signal = 'quiet' }
    @{ Name = 'wma92lossless-stereo-44k-24'; Codec = $lossless; Vbr = $true; Channels = 2; Rates = @(44100); Bits = 24; Quality = 100; Seconds = 2; Signal = 'quiet' }
    @{ Name = 'wma92lossless-mono-44k-16'; Optional = $true;   Codec = $lossless; Vbr = $true; Channels = 1; Rates = @(44100); Bits = 16; Quality = 100; Seconds = 2; Signal = 'quiet' }
    @{ Name = 'wma92lossless-5.1-48k-24';    Codec = $lossless; Vbr = $true; Channels = 6; Rates = @(48000, 44100); Bits = 24; Quality = 100; Seconds = 2; Signal = 'quiet' }
    @{ Name = 'wmavoice-cbr-mono-8k-5'; Optional = $true;      Codec = $voice; Vbr = $false; Channels = 1; Rates = @(8000, 16000); Bitrate = 5000;  Seconds = 3; Signal = 'tones' }
    @{ Name = 'wmavoice-cbr-mono-16k-20'; Optional = $true;    Codec = $voice; Vbr = $false; Channels = 1; Rates = @(16000, 22050); Bitrate = 20000; Seconds = 3; Signal = 'tones' }
    @{ Name = 'wma92-cbr-stereo-44k-128-tagged'; Codec = $standard; Vbr = $false; Channels = 2; Rates = @(44100); Bitrate = 128000; Seconds = 2; Signal = 'tones'; Title = 'Encspect test tone'; Author = 'Encspect' }
)

function New-Signal($entry, $channels, $rate, $bits, $path) {
    & $Python $signals $path --channels $channels --rate $rate --bits $bits --seconds $entry.Seconds --kind $entry.Signal
    if ($LASTEXITCODE -ne 0) { throw "$Python $signals failed with exit code $LASTEXITCODE" }
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
        status = 'failed'; optional = [bool]$entry.Optional
    }
    try {
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
        if ($encoded.Bytes -gt 0) {
            $record.status = 'ok'
            $record.file = "wma/$($entry.Name).wma"
            $record.pcm = "pcm/$($entry.Name).wav"
            $record.bytes = $encoded.Bytes
            $record.sha256 = (Get-FileHash -Algorithm SHA256 $wma).Hash.ToLowerInvariant()
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
    if ($r.status -eq 'wrong mode') { $problems += "$($r.name): the SDK offered only $($r.formatDescription) for a $($r.mode) request" }
}
$groups = $results | Where-Object { $_.status -eq 'ok' } | Group-Object sha256 | Where-Object { $_.Count -gt 1 }
foreach ($g in $groups) { $problems += ('identical output: ' + (($g.Group | ForEach-Object { $_.name }) -join ', ')) }
Stop-Transcript | Out-Null
if ($problems.Count -gt 0) {
    Write-Host '=== problems ==='
    $problems | ForEach-Object { Write-Host $_ }
    [IO.File]::WriteAllLines((Join-Path $Out 'problems.txt'), [string[]]$problems)
    exit 1
}

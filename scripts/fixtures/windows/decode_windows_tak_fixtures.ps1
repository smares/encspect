# Decodes the committed TAK fixtures with TAK's own decoder on a Windows runner and records what it gives, for crosscheck_tak_pcm.py:
#
#   powershell -File scripts/fixtures/windows/decode_windows_tak_fixtures.ps1 -Out tak-decoded
#
# Takc.exe of TAK 2.3.3 (pinned by SHA-256, scripts/fixtures/tak_tool.py; `Takc -d` is the decoder application of the SDK's decoder library, a black box) decodes each
# tests/fixtures/tak/*.tak to a WAVE file. For each, the record holds the exit status, Takc's output, and the samples of the WAVE file's data chunk: their number
# per channel and their MD5 in the canonical layout of decoded_pcm_md5 (signed, little-endian, as many whole bytes as the depth needs: 8-bit WAVE data is unsigned
# and is converted). The channels and the depth are those of the stream info, which the manifests hold; the script reads the WAVE header only for the data chunk.
#
# Writes into -Out: sdk-decoded.json (the records), environment.json and log.txt. tests/fixtures/tak/sdk-decoded.json is the file of a run copied in. A file Takc
# refuses (a damaged one) has a record with its exit status and no samples; only the encoder-written files (external-*) are compared with the manifests.
param(
    [string]$Out = (Join-Path (Get-Location) 'tak-decoded'),
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fixtureScripts = Split-Path -Parent $here
$repository = Split-Path -Parent (Split-Path -Parent $fixtureScripts)
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $tempRoot 'tak-decode-work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Start-Transcript -Path (Join-Path $Out 'log.txt') -Force | Out-Null

Add-Type -TypeDefinition (Get-Content -Raw -Encoding UTF8 (Join-Path $here 'TakTools.cs'))

function Get-Sha256($path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

Write-Host '=== the pinned archive ==='
$tool = (& $Python (Join-Path $fixtureScripts 'tak_tool.py') (Join-Path $tempRoot 'tak') | Select-Object -Last 1).Trim()
$takc = Join-Path $tool 'Applications\Takc.exe'
if (-not (Test-Path -LiteralPath $takc)) {
    Write-Host ('missing: ' + $takc)
    Stop-Transcript | Out-Null
    exit 1
}

$environment = [ordered]@{
    takVersion = '2.3.3'
    takcSha256 = Get-Sha256 $takc
    runnerImageOS = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
}
[IO.File]::WriteAllText((Join-Path $Out 'environment.json'), ($environment | ConvertTo-Json) + "`n", (New-Object Text.UTF8Encoding $false))

$files = [ordered]@{}
foreach ($tak in (Get-ChildItem -LiteralPath (Join-Path $repository 'tests\fixtures\tak') -Filter '*.tak' | Sort-Object Name)) {
    Write-Host ('=== ' + $tak.Name + ' ===')
    $manifestPath = Join-Path $tak.DirectoryName ($tak.BaseName + '.expected.json')
    $manifest = Get-Content -Raw -Encoding UTF8 $manifestPath | ConvertFrom-Json
    $record = [ordered]@{ sha256 = Get-Sha256 $tak.FullName }
    $decoded = Join-Path $work ($tak.BaseName + '.wav')
    Remove-Item -LiteralPath $decoded -Force -ErrorAction SilentlyContinue
    $run = [Encspect.TakRun]::Run($takc, ('-d -overwrite "' + $tak.FullName + '" "' + $decoded + '"'), $null, $work, 300000)
    $record['exit'] = $run.ExitCode
    $record['output'] = $run.Stdout.Trim()
    Write-Host ('exit ' + $run.ExitCode + ' ' + $run.Stdout.Trim())
    if ($manifest.stream_info -and (Test-Path -LiteralPath $decoded)) {
        $span = [Encspect.TakRun]::WavData($decoded)
        if ($span) {
            $bytes = [IO.File]::ReadAllBytes($decoded)
            $bits = [int]$manifest.stream_info.bits_per_sample
            $channels = [int]$manifest.stream_info.channels
            $data = New-Object byte[] $span[1]
            [Array]::Copy($bytes, $span[0], $data, 0, $span[1])
            if ($bits -eq 8) { for ($i = 0; $i -lt $data.Length; $i++) { $data[$i] = [byte](($data[$i] - 128) -band 255) } }
            $md5 = [Security.Cryptography.MD5]::Create()
            $record['pcmMd5'] = ([BitConverter]::ToString($md5.ComputeHash($data)) -replace '-', '').ToLowerInvariant()
            $record['samples'] = [long]($span[1] / ($channels * ($bits / 8)))
        }
    }
    $files[$tak.Name] = $record
}

$result = [ordered]@{ tool = 'Takc.exe 2.3.3 -d'; files = $files }
[IO.File]::WriteAllText((Join-Path $Out 'sdk-decoded.json'), ($result | ConvertTo-Json -Depth 6) + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host ('=== ' + $files.Count + ' files ===')
Stop-Transcript | Out-Null

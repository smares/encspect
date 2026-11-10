# Encodes the project's TAK fixtures with Takc.exe of TAK 2.3.3 (pinned by SHA-256, scripts/fixtures/tak_tool.py) on a Windows runner:
# the presets, sample formats, frame size limits, wave data variants, pipe encoding, an APEv2 tag and a lossyWAV output
# (scripts/fixtures/tak_cases.py writes the signals and the plan). Takc.exe and the SDK library run here and nowhere else, as
# black boxes. Runs under Windows PowerShell 5.1 (powershell.exe), which the Windows runners have, and needs Python 3.
#
#   powershell -File scripts/fixtures/windows/make_windows_tak_fixtures.ps1 -Out tak-out
#
# Writes into -Out:
#   tak/<case>.tak      the encoded files (the first encode of each case)
#   pcm/<input>.wav     the WAV each case was encoded from (lossyWAV's output for the lossyWAV case)
#   encodes.json        one entry per case: arguments, input, output with SHA-256, whether a second encode wrote the same bytes,
#                       Takc's output of -fi -fim0 and -fim5, the decode check, what the SDK library says (Probe), and why a case failed
#   environment.json    Windows version, runner image, the tools' versions and SHA-256
#   plan.json           the plan tak_cases.py wrote
#   log.txt             everything the script printed
#
# A case that fails is logged and skipped; the others still run. Cases marked optional may fail without failing the run;
# any other failure makes the script exit with 1 after the files are written, so that the artifact shows what happened.
param(
    [string]$Out = (Join-Path (Get-Location) 'tak-out'),
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fixtures = Split-Path -Parent $here
New-Item -ItemType Directory -Force -Path (Join-Path $Out 'tak'), (Join-Path $Out 'pcm') | Out-Null
$Out = (Resolve-Path $Out).Path
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $tempRoot 'tak-work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Start-Transcript -Path (Join-Path $Out 'log.txt') -Force | Out-Null

Add-Type -TypeDefinition (Get-Content -Raw -Encoding UTF8 (Join-Path $here 'TakTools.cs'))

function Write-Json($value, $path) {
    $text = $value | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($path, $text + "`n", (New-Object Text.UTF8Encoding $false))
}

function Get-Sha256($path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Quote-Argument([string]$argument) {
    if ($argument -match '[\s"]' -or $argument.Length -eq 0) {
        return '"' + ($argument -replace '"', '\"') + '"'
    }
    return $argument
}

function Invoke-Tool([string]$exe, [string[]]$arguments, [string]$stdinPath, [string]$workingDirectory) {
    $line = ($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
    Write-Host ('> ' + (Split-Path -Leaf $exe) + ' ' + $line + $(if ($stdinPath) { '  < ' + (Split-Path -Leaf $stdinPath) } else { '' }))
    $run = [Encspect.TakRun]::Run($exe, $line, $(if ($stdinPath) { $stdinPath } else { $null }), $workingDirectory, 300000)
    if ($run.Stdout) { Write-Host $run.Stdout.TrimEnd() }
    if ($run.Stderr) { Write-Host ('stderr: ' + $run.Stderr.TrimEnd()) }
    if ($run.Error) { Write-Host ('error: ' + $run.Error) }
    Write-Host ('exit ' + $run.ExitCode + $(if ($run.TimedOut) { ' (timed out)' } else { '' }))
    return $run
}

Write-Host '=== the pinned archive ==='
$tool = (& $Python (Join-Path $fixtures 'tak_tool.py') (Join-Path $tempRoot 'tak') | Select-Object -Last 1).Trim()
Write-Host $tool
$takc = Join-Path $tool 'Applications\Takc.exe'
$library = Join-Path $tool 'Deco_Lib\x64\tak_deco_lib.dll'
foreach ($path in @($takc, $library)) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ('missing: ' + $path)
        Stop-Transcript | Out-Null
        exit 1
    }
}

Write-Host '=== environment ==='
$os = Get-CimInstance Win32_OperatingSystem
$versionRun = Invoke-Tool $takc @('-version') $null $work
$environment = [ordered]@{
    windowsCaption     = $os.Caption
    windowsVersion     = $os.Version
    windowsBuild       = $os.BuildNumber
    architecture       = $env:PROCESSOR_ARCHITECTURE
    runnerImageOS      = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    runnerOS           = $env:RUNNER_OS
    powershell         = $PSVersionTable.PSVersion.ToString()
    takVersion         = '2.3.3'
    takcVersionOutput  = $versionRun.Stdout.Trim()
    takcSha256         = Get-Sha256 $takc
    libraryPath        = 'Deco_Lib/x64/tak_deco_lib.dll'
    librarySha256      = Get-Sha256 $library
    libraryFileVersion = (Get-Item $library).VersionInfo.FileVersion
    streamInfoStructSize = [Encspect.TakSdk]::LibrarySize()
}
$environment.GetEnumerator() | ForEach-Object { Write-Host ('{0}: {1}' -f $_.Key, $_.Value) }

Write-Host '=== the signals and the plan ==='
& $Python (Join-Path $fixtures 'tak_cases.py') $Out
$plan = Get-Content -Raw -Encoding UTF8 (Join-Path $Out 'plan.json') | ConvertFrom-Json

# lossyWAV 1.4.2, the official Windows build from the private repository (ENCSPECT_PRIVATE_TOKEN), for the one case that needs it.
$lossywav = $null
if ($plan.cases | Where-Object { $_.lossywav }) {
    Write-Host '=== lossyWAV ==='
    try {
        $lossywav = (& $Python (Join-Path $fixtures 'lossywav_tool.py') '--windows' (Join-Path $tempRoot 'lossywav') | Select-Object -Last 1).Trim()
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $lossywav)) { $lossywav = $null }
    }
    catch {
        Write-Host ('lossyWAV could not be fetched: ' + $_.Exception.Message)
        $lossywav = $null
    }
    if ($lossywav) {
        $environment['lossywavSha256'] = Get-Sha256 $lossywav
        Write-Host ('lossyWAV: ' + $lossywav)
    }
}
Write-Json $environment (Join-Path $Out 'environment.json')

function Test-File($path) { return (Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Length -gt 0) }

$records = New-Object System.Collections.ArrayList
$failures = New-Object System.Collections.ArrayList

foreach ($case in $plan.cases) {
    Write-Host ('=== ' + $case.name + ' ===')
    $record = [ordered]@{
        name      = $case.name
        mode      = $case.mode
        arguments = @($case.arguments)
        input     = $case.input
        optional  = $case.optional
        status    = 'failed'
    }
    try {
        $source = Join-Path (Join-Path $Out 'pcm') ($case.input + '.wav')
        if ($case.lossywav) {
            if (-not $lossywav) { throw 'lossyWAV 1.4.2 is not available' }
            $lwDir = Join-Path $work ('lossywav-' + $case.name)
            New-Item -ItemType Directory -Force -Path $lwDir | Out-Null
            Copy-Item -LiteralPath $source -Destination (Join-Path $lwDir 'source.wav') -Force
            $lwRun = Invoke-Tool $lossywav (@('source.wav') + @($case.lossywav.arguments)) $null $lwDir
            $lossy = Get-ChildItem -LiteralPath $lwDir -Filter '*.lossy.wav' | Select-Object -First 1
            if (-not $lossy) { throw ('lossyWAV wrote no .lossy.wav: ' + $lwRun.Stdout + $lwRun.Stderr) }
            $source = Join-Path (Join-Path $Out 'pcm') ($case.name + '.wav')
            Copy-Item -LiteralPath $lossy.FullName -Destination $source -Force
            $record['lossywav'] = [ordered]@{
                arguments = @($case.lossywav.arguments)
                source    = $case.lossywav.from
                exit      = $lwRun.ExitCode
                output    = $lwRun.Stdout.Trim()
            }
            $record['input'] = $case.name
        }
        $record['inputSha256'] = Get-Sha256 $source
        $record['inputSize'] = (Get-Item -LiteralPath $source).Length

        $fileName = $case.name + '.tak'
        $output = Join-Path (Join-Path $Out 'tak') $fileName
        $second = Join-Path $work ($case.name + '.2.tak')
        foreach ($stale in @($output, $second, ($output + '.tak'), ($second + '.tak'))) { Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue }

        $encodes = @()
        foreach ($target in @($output, $second)) {
            if ($case.mode -eq 'pipe') {
                $arguments = @('-e') + @($case.arguments) + @('-overwrite', '-', $target)
                $run = $null
                # Takc answered "Wave file not supported" to the bytes written to its standard input from .NET (run 42719141357), so the input is redirected
                # from the file first (run 42719547527 used it); the other ways follow if it fails, and the record names the one that worked.
                foreach ($method in @('redirect', 'stdin', 'type')) {
                    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
                    if ($method -eq 'stdin') {
                        $run = Invoke-Tool $takc $arguments $source $work
                    }
                    else {
                        $takcLine = (@($takc) + $arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
                        $shell = if ($method -eq 'redirect') { '"' + $takcLine + ' < ' + (Quote-Argument $source) + '"' } else { '"type ' + (Quote-Argument $source) + ' | ' + $takcLine + '"' }
                        Write-Host ('> cmd.exe /s /c ' + $shell)
                        $run = [Encspect.TakRun]::Run($env:ComSpec, '/s /c ' + $shell, '', $work, 300000)
                        if ($run.Stdout) { Write-Host $run.Stdout.TrimEnd() }
                        if ($run.Stderr) { Write-Host ('stderr: ' + $run.Stderr.TrimEnd()) }
                        Write-Host ('exit ' + $run.ExitCode)
                    }
                    if (Test-File $target) { break }
                }
                if (-not $record.Contains('pipeMethod')) { $record['pipeMethod'] = $method }
            }
            else {
                $arguments = @('-e') + @($case.arguments) + @('-overwrite', $source, $target)
                $run = Invoke-Tool $takc $arguments $null $work
            }
            if (-not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath ($target + '.tak'))) {
                Move-Item -LiteralPath ($target + '.tak') -Destination $target -Force
                Write-Host 'Takc appended .tak to the output name; renamed'
            }
            $encodes += , $run
        }
        $first = $encodes[0]
        $record['encode'] = [ordered]@{ exit = $first.ExitCode; output = $first.Stdout.Trim(); error = $first.Stderr.Trim() }
        if ($first.ExitCode -ne 0 -or -not (Test-File $output)) { throw ('Takc exit ' + $first.ExitCode + ' and ' + $(if (Test-File $output) { 'a file' } else { 'no file' })) }
        $record['output'] = $fileName
        $record['size'] = (Get-Item -LiteralPath $output).Length
        $record['sha256'] = Get-Sha256 $output
        $record['reproducible'] = (Test-File $second) -and ((Get-Sha256 $second) -eq $record['sha256'])

        $info0 = Invoke-Tool $takc @('-fi', '-fim0', $output) $null $work
        $info5 = Invoke-Tool $takc @('-fi', '-fim5', $output) $null $work
        $record['info'] = [ordered]@{ any = $info0.Stdout.Trim(); anyExit = $info0.ExitCode; compactRaw = $info5.Stdout.Trim(); compactRawExit = $info5.ExitCode }

        $decoded = Join-Path $work ($case.name + '.decoded.wav')
        Remove-Item -LiteralPath $decoded -Force -ErrorAction SilentlyContinue
        $decode = Invoke-Tool $takc @('-d', '-overwrite', $output, $decoded) $null $work
        $check = [ordered]@{ exit = $decode.ExitCode; output = $decode.Stdout.Trim() }
        if (Test-File $decoded) {
            $inSpan = [Encspect.TakRun]::WavData($source)
            $outSpan = [Encspect.TakRun]::WavData($decoded)
            $check['wholeFileEqual'] = ((Get-Sha256 $decoded) -eq $record['inputSha256'])
            $check['decodedSize'] = (Get-Item -LiteralPath $decoded).Length
            $check['audioEqual'] = [bool]($inSpan -and $outSpan -and $inSpan[1] -eq $outSpan[1] -and
                [Encspect.TakRun]::SameBytes($source, $inSpan[0], $decoded, $outSpan[0], $inSpan[1]))
        }
        else {
            $check['wholeFileEqual'] = $false
            $check['audioEqual'] = $false
        }
        $record['decode'] = $check
        if (@($case.arguments) -contains '-md5') {
            $test = Invoke-Tool $takc @('-t', '-md5', $output) $null $work
            $record['md5Test'] = [ordered]@{ exit = $test.ExitCode; output = $test.Stdout.Trim() }
        }

        try { $record['sdk'] = [Encspect.TakSdk]::Probe($library, $output) }
        catch { $record['sdk'] = [ordered]@{ error = $_.Exception.Message } }
        Write-Host (($record['sdk'] | ConvertTo-Json -Compress -Depth 4))

        if (-not $check['audioEqual']) { throw 'the decoded audio differs from the input' }
        $record['status'] = 'ok'
    }
    catch {
        $record['error'] = $_.Exception.Message
        Write-Host ('FAILED: ' + $_.Exception.Message)
        if (-not $case.optional) { [void]$failures.Add($case.name) }
    }
    [void]$records.Add($record)
}

Write-Json ([ordered]@{ takVersion = '2.3.3'; cases = $records.ToArray() }) (Join-Path $Out 'encodes.json')
Write-Host ('=== ' + (@($records | Where-Object { $_['status'] -eq 'ok' }).Count) + ' of ' + $records.Count + ' cases wrote a file that decodes to the input ===')
if ($failures.Count -gt 0) {
    Write-Host ('failed cases: ' + ($failures -join ', '))
    Stop-Transcript | Out-Null
    exit 1
}
Stop-Transcript | Out-Null
exit 0

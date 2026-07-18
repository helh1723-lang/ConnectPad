[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Serial,

    [ValidateSet('h264-8m', 'h265-6m', 'h265-5m', 'h265-4m')]
    [string[]]$Profiles = @('h264-8m', 'h265-6m', 'h265-5m', 'h265-4m'),

    [ValidateRange(1, 10)]
    [int]$Runs = 3,

    [ValidateRange(30, 3600)]
    [int]$DurationSeconds = 300,

    [switch]$NoUiExercise
)

$ErrorActionPreference = 'Stop'
$processPath = $env:Path
[Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
[Environment]::SetEnvironmentVariable('Path', $processPath, 'Process')
$root = Split-Path -Parent $PSScriptRoot
$adb = Join-Path $root 'tools\scrcpy\adb.exe'
$scrcpy = Join-Path $root 'tools\scrcpy\scrcpy.exe'
$sessionDirectory = Join-Path $root ("artifacts\benchmarks\{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$manifestPath = Join-Path $sessionDirectory 'runs.csv'

if (-not (Test-Path -LiteralPath $adb) -or -not (Test-Path -LiteralPath $scrcpy)) {
    throw 'Missing bundled adb.exe or scrcpy.exe.'
}

$endpoint = $null
if (-not [Uri]::TryCreate("tcp://$Serial", [UriKind]::Absolute, [ref]$endpoint) -or $endpoint.Port -lt 1) {
    throw 'Serial must be a wireless ADB endpoint such as 192.168.1.8:37123.'
}

if (Get-Process -Name 'scrcpy' -ErrorAction SilentlyContinue) {
    throw 'Close every running scrcpy window before starting an isolated benchmark.'
}

New-Item -ItemType Directory -Path $sessionDirectory | Out-Null
@(
    'profile,run,sample,latency_ms,notes'
    '# Fill at least 100 high-speed-camera samples per compared profile. Remove this comment row before analysis.'
) | Set-Content -LiteralPath (Join-Path $sessionDirectory 'latency-samples.csv') -Encoding UTF8

function Get-Number([string]$Text, [string]$Pattern) {
    $match = [regex]::Match($Text, $Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return $null
    }

    return [double]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Invoke-CapturedProcess(
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$OutputPath,
    [string]$ErrorPath
) {
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments `
        -WorkingDirectory (Split-Path -Parent $FilePath) `
        -RedirectStandardOutput $OutputPath -RedirectStandardError $ErrorPath `
        -WindowStyle Hidden -Wait -PassThru
    $output = Get-Content -LiteralPath $OutputPath -Raw -ErrorAction SilentlyContinue
    $errorOutput = Get-Content -LiteralPath $ErrorPath -Raw -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Text = @($output, $errorOutput) -join [Environment]::NewLine
    }
}

function Save-WifiSnapshot([string]$Path) {
    $capture = Invoke-CapturedProcess $adb @('-s', $Serial, 'shell', 'cmd', 'wifi', 'status') `
        $Path "$Path.error.txt"
    $text = $capture.Text
    $text | Set-Content -LiteralPath $Path -Encoding UTF8

    $frequency = Get-Number $text '\bFrequency:\s*(\d+)\s*MHz'
    $rssi = Get-Number $text '\bRSSI:\s*(-?\d+)'
    $txSpeed = Get-Number $text '\bTx Link speed:\s*(\d+)\s*Mbps'
    $successful = Get-Number $text '\bsuccessfulTxPacketsPerSecond:\s*([\d.eE+-]+)'
    $retried = Get-Number $text '\bretriedTxPacketsPerSecond:\s*([\d.eE+-]+)'
    $lost = Get-Number $text '\blostTxPacketsPerSecond:\s*([\d.eE+-]+)'
    $retryPercent = $null
    $lossPercent = $null
    if ($null -ne $successful -and $null -ne $retried -and $null -ne $lost) {
        $total = $successful + $retried + $lost
        if ($total -gt 0) {
            $retryPercent = $retried / $total * 100
            $lossPercent = $lost / $total * 100
        }
    }

    return [pscustomobject]@{
        FrequencyMhz = $frequency
        RssiDbm = $rssi
        TxLinkSpeedMbps = $txSpeed
        RetryPercent = $retryPercent
        LossPercent = $lossPercent
    }
}

$previousAdbLibusb = $env:ADB_LIBUSB
$env:ADB_LIBUSB = '1'
try {
    $encoderCapture = Invoke-CapturedProcess $scrcpy @("--serial=$Serial", '--list-encoders') `
        (Join-Path $sessionDirectory 'encoders-output.txt') `
        (Join-Path $sessionDirectory 'encoders-error.txt')
    if ($encoderCapture.ExitCode -ne 0) {
        throw "scrcpy --list-encoders failed with exit code $($encoderCapture.ExitCode)."
    }

    $encoderOutput = $encoderCapture.Text
    $encoderOutput | Set-Content -LiteralPath (Join-Path $sessionDirectory 'encoders.txt') -Encoding UTF8
    $hardwareH265Encoder = $null
    foreach ($line in ($encoderOutput -split "`r?`n")) {
        if ($line -match '--video-codec=h265' -and $line -match '\(hw\)' -and
            $line -match "--video-encoder=(?:'(?<quoted>[^']+)'|(?<plain>\S+))") {
            $hardwareH265Encoder = if ($Matches.quoted) { $Matches.quoted } else { $Matches.plain }
            break
        }
    }

    if (($Profiles | Where-Object { $_ -like 'h265-*' }) -and -not $hardwareH265Encoder) {
        throw 'No hardware H.265 encoder was reported. Run only -Profiles h264-8m on this device.'
    }

    [pscustomobject]@{
        StartedAt = (Get-Date).ToString('o')
        Serial = $Serial
        Profiles = $Profiles
        Runs = $Runs
        DurationSeconds = $DurationSeconds
        HardwareH265Encoder = $hardwareH265Encoder
        MaxSize = 1920
        MaxFps = 60
        UiExercise = -not $NoUiExercise
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sessionDirectory 'session.json') -Encoding UTF8

    if (-not $NoUiExercise) {
        $sizeCapture = Invoke-CapturedProcess $adb @('-s', $Serial, 'shell', 'wm', 'size') `
            (Join-Path $sessionDirectory 'wm-size-output.txt') `
            (Join-Path $sessionDirectory 'wm-size-error.txt')
        if ($sizeCapture.Text -notmatch '(\d+)x(\d+)') {
            throw 'Could not read the device display size for the UI exercise.'
        }

        $swipeX = [math]::Round([int]$Matches[1] / 2)
        $topY = [math]::Round([int]$Matches[2] * 0.25)
        $bottomY = [math]::Round([int]$Matches[2] * 0.75)
    }

    $totalRuns = $Profiles.Count * $Runs
    $completedRuns = 0
    foreach ($profile in $Profiles) {
        if ($profile -notmatch '^(h264|h265)-(\d+)m$') {
            throw "Unsupported profile: $profile"
        }

        $codec = $Matches[1]
        $bitRateMbps = [int]$Matches[2]
        for ($run = 1; $run -le $Runs; $run++) {
            $completedRuns++
            Write-Progress -Activity 'ConnectPad wireless benchmark' -Status "$profile run $run of $Runs" -PercentComplete (($completedRuns - 1) / $totalRuns * 100)

            $runDirectory = Join-Path $sessionDirectory ("{0}-run{1}" -f $profile, $run)
            New-Item -ItemType Directory -Path $runDirectory | Out-Null
            $before = Save-WifiSnapshot (Join-Path $runDirectory 'wifi-before.txt')

            $scrcpyArguments = @(
                "--serial=$Serial"
                "--video-codec=$codec"
                "--video-bit-rate=$($bitRateMbps)M"
                '--max-fps=60'
                '--max-size=1920'
                '--no-audio'
                '--keep-active'
                '--no-downsize-on-error'
                '--print-fps'
                "--time-limit=$DurationSeconds"
                '--record=stream.mp4'
                "--window-title=ConnectPad-Benchmark-$profile-run$run"
            )
            if ($codec -eq 'h265') {
                $scrcpyArguments += "--video-encoder=$hardwareH265Encoder"
            }

            $pingProcess = $null
            $streamProcess = $null
            $streamOutputTask = $null
            $streamErrorTask = $null
            $streamExitCode = -1
            $reportedFrames = 0
            $averageFps = 0
            $framesSkipped = 0
            $startedAt = Get-Date
            try {
                if (-not $NoUiExercise) {
                    Start-Process -FilePath $adb `
                        -ArgumentList @('-s', $Serial, 'shell', 'am', 'start', '-a', 'android.settings.SETTINGS') `
                        -WindowStyle Hidden -Wait | Out-Null
                }

                $pingProcess = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\PING.EXE') `
                    -ArgumentList @('-t', $endpoint.Host) `
                    -RedirectStandardOutput (Join-Path $runDirectory 'ping.txt') `
                    -RedirectStandardError (Join-Path $runDirectory 'ping-error.txt') `
                    -WindowStyle Hidden -PassThru

                $startInfo = New-Object Diagnostics.ProcessStartInfo
                $startInfo.FileName = $scrcpy
                $startInfo.Arguments = $scrcpyArguments -join ' '
                $startInfo.WorkingDirectory = $runDirectory
                $startInfo.UseShellExecute = $false
                $startInfo.CreateNoWindow = $true
                $startInfo.RedirectStandardOutput = $true
                $startInfo.RedirectStandardError = $true
                $streamProcess = New-Object Diagnostics.Process
                $streamProcess.StartInfo = $startInfo
                if (-not $streamProcess.Start()) {
                    throw 'Could not start scrcpy.'
                }

                $streamOutputTask = $streamProcess.StandardOutput.ReadToEndAsync()
                $streamErrorTask = $streamProcess.StandardError.ReadToEndAsync()
                if ($NoUiExercise) {
                    $streamProcess.WaitForExit()
                }
                else {
                    $swipeUp = $true
                    while (-not $streamProcess.HasExited) {
                        $fromY = if ($swipeUp) { $bottomY } else { $topY }
                        $toY = if ($swipeUp) { $topY } else { $bottomY }
                        Start-Process -FilePath $adb `
                            -ArgumentList @('-s', $Serial, 'shell', 'input', 'swipe', $swipeX, $fromY, $swipeX, $toY, '700') `
                            -WindowStyle Hidden -Wait | Out-Null
                        $swipeUp = -not $swipeUp
                        $streamProcess.Refresh()
                    }

                    $streamProcess.WaitForExit()
                }

                $streamProcess.Refresh()
                $streamExitCode = $streamProcess.ExitCode
                $streamOutput = $streamOutputTask.Result
                $streamOutput | Set-Content -LiteralPath (Join-Path $runDirectory 'scrcpy-output.txt') -Encoding UTF8
                $streamErrorTask.Result | Set-Content -LiteralPath (Join-Path $runDirectory 'scrcpy-error.txt') -Encoding UTF8
                $fpsValues = [regex]::Matches($streamOutput, '(?m)^INFO: (\d+) fps') |
                    ForEach-Object { [int]$_.Groups[1].Value }
                if ($fpsValues.Count -gt 0) {
                    $reportedFrames = ($fpsValues | Measure-Object -Sum).Sum
                    $averageFps = [math]::Round(($fpsValues | Measure-Object -Average).Average, 1)
                }

                $framesSkipped = [int](([regex]::Matches($streamOutput, '\(\+(\d+) frames skipped\)') |
                    ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum)
            }
            finally {
                if ($null -ne $pingProcess -and -not $pingProcess.HasExited) {
                    Stop-Process -Id $pingProcess.Id -Force
                    $pingProcess.WaitForExit()
                }
            }

            $after = Save-WifiSnapshot (Join-Path $runDirectory 'wifi-after.txt')
            [pscustomobject]@{
                StartedAt = $startedAt.ToString('o')
                Profile = $profile
                Run = $run
                Codec = $codec
                BitRateMbps = $bitRateMbps
                Encoder = if ($codec -eq 'h265') { $hardwareH265Encoder } else { '' }
                RequestedSeconds = $DurationSeconds
                ActualSeconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
                ExitCode = $streamExitCode
                ReportedFrames = $reportedFrames
                AverageFps = $averageFps
                FramesSkipped = $framesSkipped
                RecordingBytes = if (Test-Path -LiteralPath (Join-Path $runDirectory 'stream.mp4')) {
                    (Get-Item -LiteralPath (Join-Path $runDirectory 'stream.mp4')).Length
                } else { 0 }
                FrequencyMhzBefore = $before.FrequencyMhz
                RssiDbmBefore = $before.RssiDbm
                TxLinkSpeedMbpsBefore = $before.TxLinkSpeedMbps
                RetryPercentBefore = $before.RetryPercent
                LossPercentBefore = $before.LossPercent
                FrequencyMhzAfter = $after.FrequencyMhz
                RssiDbmAfter = $after.RssiDbm
                TxLinkSpeedMbpsAfter = $after.TxLinkSpeedMbps
                RetryPercentAfter = $after.RetryPercent
                LossPercentAfter = $after.LossPercent
                OutputDirectory = $runDirectory
            } | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8 -Append
        }
    }

    Write-Progress -Activity 'ConnectPad wireless benchmark' -Completed
    Write-Output $sessionDirectory
}
finally {
    if ($null -eq $previousAdbLibusb) {
        Remove-Item Env:ADB_LIBUSB -ErrorAction SilentlyContinue
    }
    else {
        $env:ADB_LIBUSB = $previousAdbLibusb
    }
}

param(
    [string]$Godot = "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe",
    [double]$Duration = 6,
    [int]$Speed = 600,
    [switch]$StationaryOnly,
    [switch]$MoveOnly,
    [switch]$FeatureSweep
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

function Stop-Godot {
    Get-Process -Name "Godot*" -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Invoke-ReviewProfile {
    param(
        [string]$Name,
        [string[]]$UserArgs
    )

    Write-Host "===== $Name ====="
    $engineArgs = @("--path", $Root, "--rendering-driver", "vulkan", "scenes/review.tscn", "--") + $UserArgs
    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $out = & $Godot @engineArgs 2>&1
    $nativeExitCode = $LASTEXITCODE
    $ErrorActionPreference = $oldErrorActionPreference
    $out | Select-String -Pattern "PROFILE:|PROFILE-PERCENTILES|PROFILE-RENDER|PROFILE-SPIKE|PROFILE-STREAM|PROFILE-CDLOD|ERROR:|SCRIPT ERROR:"
    if ($nativeExitCode -ne 0) { throw "Godot profile failed for '$Name' with exit code $nativeExitCode" }
}

Stop-Godot

$baseProfile = "--profile=$Duration"
$moveProfile = @($baseProfile, "--profmove", "--profspeed=$Speed")

if ($StationaryOnly) {
    Invoke-ReviewProfile "stationary default" @($baseProfile)
    exit
}

if ($MoveOnly) {
    Invoke-ReviewProfile "$Speed m/s default" $moveProfile
    exit
}

if ($FeatureSweep) {
    Invoke-ReviewProfile "stationary default" @($baseProfile)
    Invoke-ReviewProfile "stationary clouds off" @($baseProfile, "--clouds=0")
    Invoke-ReviewProfile "stationary shadow on" @($baseProfile, "--shadow=1")
    Invoke-ReviewProfile "stationary shadow on + clouds off" @($baseProfile, "--shadow=1", "--clouds=0")
    Invoke-ReviewProfile "$Speed m/s default" $moveProfile
    Invoke-ReviewProfile "$Speed m/s clouds off" ($moveProfile + "--clouds=0")
    Invoke-ReviewProfile "$Speed m/s shadow on" ($moveProfile + "--shadow=1")
    Invoke-ReviewProfile "$Speed m/s shadow on + clouds off" ($moveProfile + @("--shadow=1", "--clouds=0"))
    exit
}

Invoke-ReviewProfile "stationary default" @($baseProfile)
Invoke-ReviewProfile "$Speed m/s default" $moveProfile

param(
    [string]$Godot = "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$SceneArgs
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if ($null -eq $SceneArgs) { $SceneArgs = @() }

Get-Process -Name "Godot*" -ErrorAction SilentlyContinue | Stop-Process -Force

$engineArgs = @("--path", $Root, "--rendering-driver", "vulkan", "scenes/review.tscn", "--") + $SceneArgs
Start-Process -FilePath $Godot -ArgumentList $engineArgs -WorkingDirectory $Root

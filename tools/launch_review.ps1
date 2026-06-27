param(
    [string]$Godot = "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$SceneArgs
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if ($null -eq $SceneArgs) { $SceneArgs = @() }
. "$PSScriptRoot\godot_vulkan_env.ps1"

Get-Process -Name "Godot*" -ErrorAction SilentlyContinue | Stop-Process -Force

$engineArgs = @("--path", $Root, "--rendering-driver", "vulkan", "scenes/review.tscn", "--") + $SceneArgs
Clear-GodotVulkanCaptureEnv
Start-Process -FilePath $Godot -ArgumentList $engineArgs -WorkingDirectory $Root

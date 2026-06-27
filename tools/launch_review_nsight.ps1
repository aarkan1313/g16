param(
    [string]$Godot = "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe",
    [string]$NsightRoot = "C:\Program Files\NVIDIA Corporation\Nsight Graphics 2026.2.0",
    [switch]$OpenUi,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$SceneArgs
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if ($null -eq $SceneArgs) { $SceneArgs = @() }
. "$PSScriptRoot\godot_vulkan_env.ps1"

$target = Join-Path $NsightRoot "target\windows-desktop-nomad-x64"
$layerName = "VK_LAYER_NV_nomad_release_public_2026_2_0"
$layerJson = Join-Path $target "$layerName.json"
$ui = Join-Path $NsightRoot "host\windows-desktop-nomad-x64\ngfx-ui.exe"

if (!(Test-Path $layerJson)) { throw "Nsight Vulkan layer manifest not found: $layerJson" }

Get-Process -Name "Godot*" -ErrorAction SilentlyContinue | Stop-Process -Force
Clear-GodotVulkanCaptureEnv

$env:VK_LAYER_PATH = $target
$env:VK_INSTANCE_LAYERS = $layerName
$env:ENABLE_VK_LAYER_NV_nomad_release_public_2026_2_0 = "1"
$env:PATH = "$target;$env:PATH"

if ($OpenUi -and (Test-Path $ui)) {
    Start-Process -FilePath $ui -WorkingDirectory (Split-Path -Parent $ui)
}

$engineArgs = @("--path", $Root, "--rendering-driver", "vulkan", "scenes/review.tscn", "--") + $SceneArgs
Start-Process -FilePath $Godot -ArgumentList $engineArgs -WorkingDirectory $Root

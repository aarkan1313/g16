function Clear-GodotVulkanCaptureEnv {
    $names = @(
        "VK_INSTANCE_LAYERS",
        "VK_LAYER_PATH",
        "VK_ADD_LAYER_PATH",
        "VK_LOADER_LAYERS_ENABLE",
        "VK_LOADER_LAYERS_DISABLE",
        "ENABLE_VK_LAYER_NV_nomad",
        "ENABLE_VK_LAYER_NV_nomad_release_public_2026_2_0",
        "ENABLE_VK_LAYER_NV_ngfx_capture_release_public_2026_2_0",
        "ENABLE_VK_LAYER_NV_GPU_Trace_release_public_2026_2_0",
        "ENABLE_VK_LAYER_NV_shader_debugger_release_public_2026_2_0"
    )
    foreach ($name in $names) {
        if (Test-Path "Env:$name") {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
    }
}


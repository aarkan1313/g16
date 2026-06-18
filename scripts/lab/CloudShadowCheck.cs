using Godot;
using System;
using Godot.Collections;

namespace WG16.Lab;

/// Numeric self-check for cloud-shadow correctness (the user asked to PROVE it with math,
/// not by eyeballing). Runs on a LOCAL RenderingDevice (so it can read back — must run
/// WINDOWED, like every local-RD compute here), dispatching shaders/cloud_shadow_check.glsl
/// over a world-XZ grid. For each cell it gets the production sun-visibility (the shadow
/// value) AND the vertical cloud density overhead, then asserts the correctness relationship
/// numerically:
///   PASS if Pearson correlation(verticalDensity, 1 - sunVisibility) is strongly POSITIVE
///   (more cloud overhead ⇒ darker shadow). A near-zero or negative r means the shadow is
///   NOT tracking the clouds (the bug we kept eyeballing).
/// One job: prove/refute shadow accuracy and print PASS/FAIL + r. No scene, no visuals.
public static class CloudShadowCheck
{
    public static void Run(CloudParams p, Vector3 sunDir, float regionM, float groundHeight, Vector2 windOffset)
    {
        const int Grid = 96;   // 96² sample points — plenty for a correlation
        RenderingDevice rd = RenderingServer.CreateLocalRenderingDevice();
        if (rd == null) { GD.PrintErr("[shadowcheck] no local RD (running headless?) — must run windowed"); return; }

        // compile the check shader
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/cloud_shadow_check.glsl"))
            .Replace("#[compute]\r\n", string.Empty).Replace("#[compute]\n", string.Empty);
        var spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src });
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) { GD.PrintErr("[shadowcheck] shader: " + spirv.CompileErrorCompute); return; }
        Rid shader = rd.ShaderCreateFromSpirV(spirv, "cloud_shadow_check");
        Rid pipeline = rd.ComputePipelineCreate(shader);

        // bake noise + weather onto this local RD (same data the renderer uses)
        var noise = new CloudNoiseCompute();
        (byte[] shapeB, int shapeRes, byte[] detailB, int detailRes) = noise.BakeRaw();
        byte[] weatherB = CloudWeather.BakeRaw();
        noise.Dispose();
        Rid shapeTex = Make3D(rd, shapeRes, shapeB);
        Rid detailTex = Make3D(rd, detailRes, detailB);
        Rid weatherTex = Make2D(rd, CloudWeather.Res, CloudWeather.Res, weatherB);
        Rid sampler = rd.SamplerCreate(new RDSamplerState {
            MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.Repeat, RepeatV = RenderingDevice.SamplerRepeatMode.Repeat, RepeatW = RenderingDevice.SamplerRepeatMode.Repeat });

        Rid outBuf = rd.StorageBufferCreate((uint)(Grid * Grid * 4 * sizeof(float)));
        byte[] pb = BuildParams(p, sunDir.Normalized(), regionM, groundHeight, windOffset, Grid);
        Rid paramBuf = rd.StorageBufferCreate((uint)pb.Length, pb);

        var uOut = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; uOut.AddId(outBuf);
        var uShape = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; uShape.AddId(sampler); uShape.AddId(shapeTex);
        var uDetail = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; uDetail.AddId(sampler); uDetail.AddId(detailTex);
        var uWeather = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 }; uWeather.AddId(sampler); uWeather.AddId(weatherTex);
        var uParam = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 }; uParam.AddId(paramBuf);
        Rid set = rd.UniformSetCreate(new Array<RDUniform> { uOut, uShape, uDetail, uWeather, uParam }, shader, 0);

        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, pipeline);
        rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((Grid + 7) / 8);
        rd.ComputeListDispatch(list, groups, groups, 1);
        rd.ComputeListEnd();
        rd.Submit(); rd.Sync();

        byte[] outBytes = rd.BufferGetData(outBuf);
        var f = new float[Grid * Grid * 4];
        System.Buffer.BlockCopy(outBytes, 0, f, 0, Math.Min(outBytes.Length, f.Length * sizeof(float)));

        // correlate vertical density (y) vs shadow darkness (1 - x)
        int n = Grid * Grid;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        int shadowed = 0; double maxDens = 0;
        for (int i = 0; i < n; i++)
        {
            double dens = f[i * 4 + 1];
            double dark = 1.0 - f[i * 4 + 0];
            sx += dens; sy += dark; sxx += dens * dens; syy += dark * dark; sxy += dens * dark;
            if (dark > 0.05) shadowed++;
            if (dens > maxDens) maxDens = dens;
        }
        double cov = sxy - sx * sy / n;
        double vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
        double r = (vx > 1e-9 && vy > 1e-9) ? cov / Math.Sqrt(vx * vy) : 0.0;
        double pctShadowed = 100.0 * shadowed / n;

        // also report the vertical-density stats + sun vector so a 0% result is diagnosable
        int hasCloud = 0; for (int i = 0; i < n; i++) { if (f[i * 4 + 1] > 0.001) hasCloud++; }
        Vector3 Ln = sunDir.Normalized();
        bool pass = r > 0.6;   // strong positive: cloud overhead ⇒ shadow
        GD.Print($"[shadowcheck] n={n} grid={Grid}² coverage={p.Coverage:F2}  sun=({Ln.X:F2},{Ln.Y:F2},{Ln.Z:F2})  " +
                 $"cellsWithCloudOverhead={100.0*hasCloud/n:F1}%");
        GD.Print($"[shadowcheck] corr(density, shadow-darkness) r={r:F3}  shadowed={pctShadowed:F1}%  maxVertDensity={maxDens:F3}");
        GD.Print(pass
            ? $"[shadowcheck] PASS — shadow tracks cloud overhead (r={r:F3} > 0.6). Darker ground ⇔ more cloud above."
            : $"[shadowcheck] FAIL — shadow does NOT track cloud overhead (r={r:F3} ≤ 0.6). The shadow is decoupled from the clouds.");

        rd.FreeRid(set); rd.FreeRid(outBuf); rd.FreeRid(paramBuf); rd.FreeRid(sampler);
        rd.FreeRid(shapeTex); rd.FreeRid(detailTex); rd.FreeRid(weatherTex);
        rd.FreeRid(pipeline); rd.FreeRid(shader); rd.Free();
    }

    private static Rid Make3D(RenderingDevice rd, int res, byte[] rgbaf)
    {
        var tf = new RDTextureFormat { Width = (uint)res, Height = (uint)res, Depth = (uint)res,
            TextureType = RenderingDevice.TextureType.Type3D, Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit };
        Rid rid = rd.TextureCreate(tf, new RDTextureView());
        rd.TextureUpdate(rid, 0, rgbaf);
        return rid;
    }
    private static Rid Make2D(RenderingDevice rd, int w, int h, byte[] rgbaf)
    {
        var tf = new RDTextureFormat { Width = (uint)w, Height = (uint)h, Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit };
        Rid rid = rd.TextureCreate(tf, new RDTextureView());
        rd.TextureUpdate(rid, 0, rgbaf);
        return rid;
    }

    private static byte[] BuildParams(CloudParams p, Vector3 sun, float regionM, float groundH, Vector2 wind, int grid)
    {
        // matches cloud_shadow_check.glsl ParamsBuf
        var b = new byte[24 * sizeof(float)];
        int o = 0;
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        F(sun.X); F(sun.Y); F(sun.Z); F(0f);            // sun_dir
        F(grid); F(grid);                                // grid
        F(regionM); F(16f);                              // region size, march steps
        F(p.AltitudeM); F(p.ThicknessM);
        F(p.Coverage); F(p.Density); F(p.CloudType);
        F(p.Size); F(p.Detail); F(p.DetailSize); F(p.Edge);
        F(0.45f); F(groundH);                            // strength, ground_height
        F(wind.X); F(wind.Y);                            // wind_offset
        return b;
    }
}

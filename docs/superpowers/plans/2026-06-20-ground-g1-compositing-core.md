# Ground G-1 Compositing Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kill the blocky/stair-stepped/smeary material-blend by de-quantizing the weight field (8-bit → half-float) and generating the boundary at fragment resolution, behind a toggle.

**Architecture:** `SplatCompute` bakes the two role-weightmaps as half-float (`Rgbah`) instead of 8-bit `Rgba8` (compute uses `packHalf2x16`, C# reads the bytes straight into an `Rgbah` image — no conversion). The fragment shader's weightmap blend path gains an `hq_blend` mode: the now-smooth weight `t` only biases the boundary, whose sharp shape comes from full-res material height + noise with an `fwidth`-based AA width. Plus a sampling mip/aniso verify.

**Tech Stack:** Godot 4.6 mono (C# + GLSL compute + GLSL fragment shader). GPU/visual verification, no TDD.

## Global Constraints

- **Project dir / launch:** `C:\Wg16\wg-16-project`; always `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`. ONE Godot at a time — `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` first.
- **Godot exe:** `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe` (`_console.exe` for headless `--import` / stdout).
- **Build:** `dotnet build WG16.csproj`. After a new `.cs`: build → headless `--import` before windowed.
- **No TDD — GPU/visual.** Verify = build clean → headless `--import` → windowed `--auto-shot`/`splat debug` A/B → user's live eye. The weightmap bake is **local-RD compute — won't run `--headless`**; bake/render windowed.
- **Discipline:** build to the user's eye-gate, not past it. New blend behind `hq_blend`, default OFF until the gate passes. Keepers (base field, histogram anti-tiling, placement) untouched. **Must NOT move material placement/zones — only transition quality.**
- **Perf budget:** 8 ms in-motion. Profile `--profmove`.
- **Commit by default** to `experiment/presentation`. End messages with the Co-Authored-By line.
- **Shared files (other lane edits them):** `data/lab_controls.json` — keep edits minimal/additive; at commit, stage ONLY your hunk (verify `git diff` is yours).

---

### Task 1: Half-float weightmaps (de-quantize the weight field)

**Files:**
- Modify: `shaders/splat_weights.glsl` (buffer decls + `packUnorm4x8` → `packHalf2x16`, lines ~34-38, 246-247)
- Modify: `scripts/lab/SplatCompute.cs` (weightmap buffer size + `Rgba8` → `Rgbah`, lines ~79-85, 116-117)

**Interfaces:**
- Produces: `splat_wa`/`splat_wb` bound as `Rgbah` ImageTextures (4 half-float weights/texel), sampled unchanged by `splat_weights7` in `terrain_lab.gdshader`.

- [ ] **Step 1: Pack weights as half-float in the compute shader.**

In `shaders/splat_weights.glsl`, change the two weightmap output buffers to 2 uints/cell and pack with `packHalf2x16`. Replace the buffer decls (lines ~37-38):

```glsl
// Phase A: 7 smooth role weights as HALF-FLOAT (Rgbah) — 2 uints (packHalf2x16) per texel.
// wa = roles {0,1,2,3}; wb = roles {4,5,6, spare}. Half kills the old Rgba8 quantization
// (the stair-stepped blend); filter_linear in the fragment.
layout(set = 0, binding = 3, std430) restrict writeonly buffer WOutA { uint wa[]; };
layout(set = 0, binding = 4, std430) restrict writeonly buffer WOutB { uint wb[]; };
```

And the packing (lines ~246-247) — note the index is now `wi*2`:

```glsl
    wa[wi*2u + 0u] = packHalf2x16(vec2(w[0], w[1]));
    wa[wi*2u + 1u] = packHalf2x16(vec2(w[2], w[3]));
    wb[wi*2u + 0u] = packHalf2x16(vec2(w[4], w[5]));
    wb[wi*2u + 1u] = packHalf2x16(vec2(w[6], 0.0));
```

(`packHalf2x16(vec2(a,b))` puts `a` in the low 16 bits, `b` in the high 16 — little-endian that is bytes `[a_half, b_half]`, i.e. exactly the R,G then B,A channel order `Rgbah` expects.)

- [ ] **Step 2: Allocate the half-float buffers + build the `Rgbah` images in C#.**

In `scripts/lab/SplatCompute.cs`, the two weightmap buffers are now 8 bytes/cell. Change lines ~80 and ~83 from `cells * sizeof(uint)` to `cells * 2 * sizeof(uint)`:

```csharp
        // Phase A weightmaps: 2 packed uints (Rgbah half-float) per cell (bindings 3, 4)
        Rid waBuf = _rd.StorageBufferCreate((uint)(cells * 2 * sizeof(uint)));
        var waU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 };
        waU.AddId(waBuf);
        Rid wbBuf = _rd.StorageBufferCreate((uint)(cells * 2 * sizeof(uint)));
        var wbU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 };
        wbU.AddId(wbBuf);
```

And the image creation (lines ~116-117), `Rgba8` → `Rgbah`:

```csharp
        Image waImg = Image.CreateFromData(res, res, false, Image.Format.Rgbah, waBytes);
        Image wbImg = Image.CreateFromData(res, res, false, Image.Format.Rgbah, wbBytes);
```

(Update the readback comment on line ~104 to "packed Rgbah half-float: R=w0,G=w1,B=w2,A=w3".)

- [ ] **Step 3: Build + headless import (compile-check both shaders).**

Run: `dotnet build WG16.csproj` → `0 Error(s)`; then
`"…_console.exe" --headless --path /c/Wg16/wg-16-project --import 2>&1 | grep -i error` → no `splat_weights.glsl` compile error, no C# error. (A bad `packHalf2x16`/index shows here.)

- [ ] **Step 4: A/B the mix-amt — verify the stair-stepping is gone.**

Capture the blend weight before/after isn't possible (this IS the after), so verify directly: windowed, `splat debug = mix amt`, fly close to a material transition. The `mix amt` should read **smooth gradients, no 8-bit stair-steps**. Auto-shot for the record:
`"…exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --auto-shot=c:/tmp/g1_mixamt.png --splatdebug=2 --cam=1500,350,1500,-90,0`
Expected: saved PNG; `mix amt` transitions are continuous (the stair-step banding from `Rgba8` is gone). Placement (which material where) is **unchanged** vs before.

- [ ] **Step 5: Commit.**

```bash
git add shaders/splat_weights.glsl scripts/lab/SplatCompute.cs
git commit -m "Ground G-1 T1: half-float (Rgbah) weightmaps — de-quantize the blend"
```

---

### Task 2: Sampling mip/aniso verify (component #2 — keep, confirm)

**Files:**
- Inspect: `shaders/terrain_lab.gdshader` (the `z*_alb/_nrm/_rgh/_ao` sampler hint declarations)

**Interfaces:**
- Produces: confirmation (or a one-line hint fix) that material samplers are `filter_linear_mipmap_anisotropic`.

- [ ] **Step 1: Check the sampler hints.**

Run: `grep -nE 'uniform sampler2D z[0-9]_(alb|nrm|rgh|ao)' shaders/terrain_lab.gdshader`
Expected: each declares `: source_color, filter_linear_mipmap_anisotropic, repeat_enable` (albedo `source_color`; normal/rough/ao linear). If any lacks `mipmap_anisotropic`, that is the documented "fuzz" cause.

- [ ] **Step 2: Fix only if a sampler lacks mip/aniso.**

If (and only if) a material sampler is missing the mipmap/aniso hint, add it, e.g.:

```glsl
uniform sampler2D z0_rgh : filter_linear_mipmap_anisotropic, repeat_enable;
```

If all are already correct, record "sampling verified, no change" and skip to the next task — do NOT touch the histogram path.

- [ ] **Step 3: Build + import (only if changed).**

Run: `dotnet build WG16.csproj` → `0 Error(s)`; headless `--import` → no shader error. If nothing changed, no build needed.

- [ ] **Step 4: Commit (only if changed).**

```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground G-1 T2: verify material samplers use mip+aniso (fuzz check)"
```

---

### Task 3: `hq_blend` — fragment-resolution AA interlock + stronger warp

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (add `hq_blend` + `hq_bias` uniforms, a `hq_interlock()` fn, branch the weightmap blend, strengthen the warp)
- Modify: `data/lab_controls.json` (add `hq_blend` toggle — minimal/additive, shared file)

**Interfaces:**
- Consumes: half-float `splat_weights7` (Task 1); existing `material_height`, `interlock_sharp`, `interlock_breakup`, `vnoise`, `tex_scale_m`, `splat_warp_m`/`_wl`.
- Produces: `hq_blend` toggle (default false) selecting the fragment-resolution boundary.

- [ ] **Step 1: Add the uniforms.**

Near `interlock_sharp` in `terrain_lab.gdshader`:

```glsl
uniform bool  hq_blend = false;                              // G-1: fragment-resolution AA blend
uniform float hq_bias : hint_range(0.0, 1.0) = 0.5;          // how much the (smooth) weight biases the boundary
```

- [ ] **Step 2: Add the fragment-resolution interlock function.**

Place next to `interlock_blend`:

```glsl
// G-1: boundary generated at FRAGMENT resolution. The full-res material heights (hD,hS) + their
// breakup noise define the boundary SHAPE; the smooth low-res weight `t` only BIASES its position
// (hq_bias). fwidth gives a pixel-AA transition width — no stair-step at grazing angles.
float hq_interlock(float hD, float hS, float t){
    float e = (hD - hS) + (2.0*t - 1.0) * hq_bias;   // boundary where e crosses 0
    float w = max(fwidth(e), 1e-4);                  // pixel-width AA band
    return clamp(0.5 + 0.5*e/w, 0.0, 1.0);           // == smoothstep(-w,w,e), AA
}
```

- [ ] **Step 3: Branch the weightmap blend to use it + strengthen the warp.**

In the weightmap blend path, the swarp currently uses 2 octaves; add a third finer octave so sub-4 m structure is organic. Replace the `swarp` assignment (the `vec2 swarp = ...` block) with:

```glsl
            vec2 swarp = (vec2(vnoise(wp.xz + vec2(19.0,57.0), splat_warp_wl),
                               vnoise(wp.xz + vec2(83.0,11.0), splat_warp_wl)) - 0.5)
                       + 0.5 * (vec2(vnoise(wp.xz + vec2(5.0,99.0), splat_warp_wl*0.4),
                                     vnoise(wp.xz + vec2(61.0,7.0), splat_warp_wl*0.4)) - 0.5)
                       + (hq_blend ? 0.25 * (vec2(vnoise(wp.xz + vec2(13.0,71.0), splat_warp_wl*0.15),
                                                  vnoise(wp.xz + vec2(47.0,29.0), splat_warp_wl*0.15)) - 0.5)
                                   : vec2(0.0));
```

And change the `m` assignment (the line `float m = heightblend_on ? interlock_blend(hD, hS, t, interlock_sharp) : t;`) to:

```glsl
            float m = hq_blend ? hq_interlock(hD, hS, t)
                               : (heightblend_on ? interlock_blend(hD, hS, t, interlock_sharp) : t);
```

- [ ] **Step 4: Add the lab toggle (shared file — minimal).**

In `data/lab_controls.json`, add to the Splat tab controls (near `interlock_sharp`):

```json
    { "id": "hq_blend", "label": "HQ blend (G-1)", "tab": "Splat", "type": "toggle", "param": "hq_blend", "default": false, "rand": false },
```

- [ ] **Step 5: Build + import.**

Run: `dotnet build WG16.csproj` → `0 Error(s)`; headless `--import` → no shader error; no `unknown control id 'hq_blend'` warning.

- [ ] **Step 6: A/B — verify the boundary reads fragment-resolution.**

Windowed, key 3, Splat tab → toggle `HQ blend (G-1)`. With `splat debug = mix amt`, fly close to a transition: the boundary should read **smooth + organic at any zoom, no 4 m grid, AA edge** (vs the off state). Confirm placement unchanged. Profile `--profmove` on/off — within 8 ms.

- [ ] **Step 7: Commit (stage only your hunk of lab_controls.json).**

```bash
git diff data/lab_controls.json   # confirm ONLY the hq_blend line is yours
git add shaders/terrain_lab.gdshader data/lab_controls.json
git commit -m "Ground G-1 T3: hq_blend — fragment-resolution AA interlock + stronger warp"
```

- [ ] **Step 8: Hand to the live eye-gate.**

Wire `hq_blend` into the Shift+4 ground-review bank (add a case to `ApplyGroundReview` in `TerrainLabUI.GroundReview.cs`: Shift+4 toggles `hq_blend` + banner). Drive the gate with the user (key 3, close/mid to a transition, `mix amt` smooth). On PASS: flip `hq_blend` default on (shader + lab_controls), record in NEEDS_REVIEW + DECISIONS, proceed to Phase G-2. On tweak: iterate `hq_bias` / warp / `interlock_breakup`.

---

## Self-Review

**1. Spec coverage:**
- Component #3 de-quantize (half-float weights) → Task 1. ✓
- Component #3 fragment-resolution boundary + fwidth AA + t-as-bias → Task 3 Steps 2-3. ✓
- Component #3 stronger domain warp → Task 3 Step 3. ✓
- `hq_blend` toggle, default-off-until-gated → Task 3 Steps 1,4. ✓
- Component #2 sampling mip/aniso verify → Task 2. ✓
- "Don't move placement" → asserted in Task 1 Step 4 + Task 3 Step 6 checks. ✓
- Perf gate → Task 3 Step 6. ✓
- Gate + flip-default-on-pass → Task 3 Step 8. ✓
- Risk: float→half packing → Task 1 (packHalf2x16 + Rgbah, no CPU convert). ✓
- Risk: byte-stable when off → `hq_blend` default false; Task 1 changes only precision (placement check). ✓

**2. Placeholder scan:** No TBDs. Every code step shows the code. The two "inspect/verify" steps (Task 2) name the exact grep + the conditional fix.

**3. Type consistency:** `packHalf2x16` 2-uint layout (Task 1 shader) ↔ `cells*2*sizeof(uint)` buffers + `Rgbah` image (Task 1 C#) match. `hq_blend`/`hq_bias` uniforms (Task 3 S1) ↔ `hq_interlock(hD,hS,t)` usage (S2-3) ↔ `hq_blend` lab id (S4) consistent. `hq_interlock` signature matches its call site (hD, hS, t already computed in the weightmap path).

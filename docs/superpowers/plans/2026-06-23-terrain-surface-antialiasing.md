# Terrain Surface Anti-Aliasing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the camera-fixed concentric shading "rings" on terrain by fixing the two confirmed aliasing channels — the normal-map perturbation (in-shader Toksvig + proper tangents) and the albedo speckle (distance detail-fade to mean) — both on one shared detail band.

**Architecture:** All changes live in the textured branch of `shaders/ground.gdshader` (`use_textures`). The normal map is applied through a proper world-aligned tangent frame and contributes a Toksvig roughness floor that rises with sub-pixel normal variance; albedo (and roughness) blend toward their texture mean across a shared `detail_fade(dist)` band. The existing live debug aids (G detail-fade toggle, H colored diag-mode stepper) are the eye-gate.

**Tech Stack:** Godot 4.6.2 spatial shader (GLSL-like), C# (Godot Mono) only for the already-present debug toggles.

## Global Constraints

- Shader file: `shaders/ground.gdshader`. Godot requires ONE uniform per statement (no comma lists).
- Shaders hot-compile on save; the PLAYER reads `app_userdata/"WG16 base field"/shader_cache` — a "no effect" shader edit means a stale USER shader_cache, not the project `.godot` one.
- C# changes require `dotnet build WG16.csproj` before launching the player (the debug toggles are already built; no new C# is required by this plan).
- Launch: `Godot…exe --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- <FLAGS>` — user flags MUST follow a bare `--`.
- Eye-gate canonical view: `--cdlod=1 --clouds=0 --cam=0,900,0,-35,0`. Diagnostic modes via key **J** (0 full, 1 red=base, 2 green=+albedo, 3 blue=+roughness, 4 yellow=+normalmap). Detail-fade A/B via key **G**.
- Vertex path MUST stay byte-equivalent (guards `--morphcheck --stitchcheck --streamcheck --fieldcheck` must still PASS).
- Kill all Godot processes before relaunch (stale windows have misled the eye-gate before).
- Perf budget: extra fragment cost target < 0.1 ms (a few taps + cheap math).

---

### Task 1: Proper tangent-space normal application

Replace the tangent-free `apply_nrm` hack with a world-aligned tangent frame built from the geometric normal, so the normal map perturbs lighting correctly (not by rotating xy straight into world XZ). This alone won't fully kill the rings (Toksvig in Task 2 does), but it is the correct base and is independently reviewable.

**Files:**
- Modify: `shaders/ground.gdshader` — `apply_nrm` helper (~line 250) and its two call sites in the textured `fragment()` branch (~lines 330-331 in the diag stepper / the live normal block).

**Interfaces:**
- Produces: `vec3 apply_nrm_tangent(vec3 geo_n, sampler2D nmap, vec2 uv, float strength)` — returns a world-space normal, geo_n as the base, perturbed by the tangent-space map transformed through a `(T,B,N)` basis derived from `geo_n`.

- [ ] **Step 1: Replace the helper with a proper tangent-frame version**

In `shaders/ground.gdshader`, replace the `apply_nrm` function:

```glsl
// Apply a tangent-space normal map using a world-aligned tangent frame derived from the geometric normal
// (the terrain mesh carries no tangents). For terrain a world-Z-aligned basis is stable across chunks and
// correct for near-flat ground. `strength` scales the perturbation; 0 = pure geometric normal.
vec3 apply_nrm_tangent(vec3 geo_n, sampler2D nmap, vec2 uv, float strength) {
    vec3 N = normalize(geo_n);
    vec3 T = normalize(cross(vec3(0.0, 0.0, 1.0), N));
    // Degenerate guard: if N is ~parallel to +Z, pick a different reference axis.
    if (!(dot(T, T) > 1e-6)) { T = normalize(cross(vec3(1.0, 0.0, 0.0), N)); }
    vec3 B = cross(N, T);
    vec3 ts = texture(nmap, uv).xyz * 2.0 - 1.0;       // tangent-space normal
    ts.xy *= strength;                                  // scale only the tangent deflection
    vec3 world_n = normalize(ts.x * T + ts.y * B + ts.z * N);
    return world_n;
}
```

- [ ] **Step 2: Point the diag mode-4 (yellow) normal block at the new helper**

In the `fragment()` textured branch, update the `nrm_pert` build (the two `apply_nrm(...)` calls used by mode 4) to call `apply_nrm_tangent` with the same args. Keep the same `(w1+w2)/sum` and `w0/sum` strengths:

```glsl
vec3 nrm_pert = v_normal;
nrm_pert = apply_nrm_tangent(nrm_pert, mat1_nrm, uv, (w1 + w2) / sum);
nrm_pert = apply_nrm_tangent(nrm_pert, mat0_nrm, uv, w0 / sum);
```

- [ ] **Step 3: Build (no C# change, but confirm shader parses) and launch the eye-gate**

```bash
# kill stale, then launch
powershell -Command "Get-Process | ? { \$_.ProcessName -like '*Godot*' } | Stop-Process -Force -EA SilentlyContinue"
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" \
  --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- \
  --cdlod=1 --clouds=0 --cam=0,900,0,-35,0 > /c/tmp/wg16shots/t1.txt 2>&1 &
```
Expected: scene loads, no shader compile error in `/c/tmp/wg16shots/t1.txt`. Press **J** to mode 4 (yellow). The lighting from the normal map should now look directionally correct (not skewed). Rings likely still present in yellow — Task 2 fixes that.

- [ ] **Step 4: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "Terrain AA: proper tangent-space normal application (replace tangent-free hack)"
```

---

### Task 2: In-shader Toksvig roughness boost

Add a Toksvig roughness floor driven by sub-pixel normal-map variance, so distant / high-variance pixels auto-roughen and the lit↔shadowed normal moiré (mode 4 / yellow rings) collapses. This is the AAA fix for the normal channel.

**Files:**
- Modify: `shaders/ground.gdshader` — add `toksvig_k` uniform (near the surfacing uniforms ~line 110); add a Toksvig helper; apply the roughness floor where `rgh`/`ROUGHNESS` is finalized and inside the diag mode-4 block.

**Interfaces:**
- Consumes: `apply_nrm_tangent` (Task 1).
- Produces: `float toksvig_gloss(sampler2D nmap, vec2 uv, float k)` returns gloss in (0,1]; `float toksvig_rough_floor(float gloss)` returns a roughness floor in [0,1).

- [ ] **Step 1: Add the tunable uniform**

Near the other `surf_*` / `mat_tiling` uniforms in `shaders/ground.gdshader`:

```glsl
uniform float toksvig_k = 0.25;   // Toksvig sensitivity: higher = roughen sooner as normal variance rises
```

- [ ] **Step 2: Add the Toksvig helpers**

Place above `fragment()` (after `apply_nrm_tangent`):

```glsl
// Toksvig specular anti-aliasing: the mip-filtered tangent normal shortens (|n| < 1) where sub-pixel normal
// variance is high. Map that to a gloss factor; a lower gloss → higher roughness floor → no specular/lit-shadow
// flicker at distance. `texture()` here uses the same trilinear+aniso mip the lighting sees, so this tracks the
// actual footprint automatically (this is why it kills the camera-distance rings).
float toksvig_gloss(sampler2D nmap, vec2 uv, float k) {
    vec3 ts = texture(nmap, uv).xyz * 2.0 - 1.0;   // mip-filtered → magnitude < 1 under variance
    float len = clamp(length(ts), 1e-3, 1.0);
    return len / mix(1.0, len, k);                 // k in [0,1]; k=0 → gloss=1 (no effect), k=1 → gloss=len
}
float toksvig_rough_floor(float gloss) {
    return sqrt(clamp(1.0 - gloss, 0.0, 1.0));      // variance → minimum roughness
}
```

- [ ] **Step 3: Apply the roughness floor in the LIVE (mode 0) path**

Where the textured branch finalizes roughness for the real render (mode 0 block), raise roughness by the
Toksvig floor from the dominant normal maps (mat0/mat1, the ground ones), weighted like the perturbation:

```glsl
float tk = toksvig_rough_floor(toksvig_gloss(mat1_nrm, uv, toksvig_k)) * ((w1 + w2) / sum)
         + toksvig_rough_floor(toksvig_gloss(mat0_nrm, uv, toksvig_k)) * (w0 / sum);
float rgh_aa = max(clamp(rgh, 0.04, 1.0), tk);
```
Then in the mode-0 branch set `ROUGHNESS = rgh_aa;` (replace the prior `clamp(rgh,0.04,1.0)`).

- [ ] **Step 4: Apply the floor in diag mode-4 (yellow) so the eye-gate reflects the fix**

In the mode-4 `else` block, change `ROUGHNESS = 1.0;` is already matte — instead make mode 4 show the REAL fix by
using the Toksvig floor on the perturbed normal so the gate proves the kill:

```glsl
} else {  // mode 4 (yellow): +normal-map, with Toksvig — should now be RING-FREE
    ALBEDO = vec3(0.45, 0.42, 0.10);
    ROUGHNESS = max(0.5, toksvig_rough_floor(toksvig_gloss(mat0_nrm, uv, toksvig_k)));
    NORMAL = normalize(nrm_pert);
}
```

- [ ] **Step 5: Launch eye-gate, step to mode 4 (yellow)**

```bash
powershell -Command "Get-Process | ? { \$_.ProcessName -like '*Godot*' } | Stop-Process -Force -EA SilentlyContinue"
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" \
  --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- \
  --cdlod=1 --clouds=0 --cam=0,900,0,-35,0 > /c/tmp/wg16shots/t2.txt 2>&1 &
```
Expected (USER eye-gate): press **J** to mode 4 (yellow) — the rings should be GONE or strongly reduced. If still visible, raise `toksvig_k` (tunable). This is a USER gate — do not self-certify from a downscaled shot.

- [ ] **Step 6: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "Terrain AA: in-shader Toksvig roughness boost (kills normal-map ring aliasing)"
```

---

### Task 3: Albedo distance detail-fade to mean

Make the albedo (green-mode) speckle dissolve to its texture mean across the shared detail band, tuned to fully clear — not just halve. Most of this scaffolding already exists (`tex_mean`, `detail_dissolve`, `dis`); this task tunes the band and confirms the green-mode kill.

**Files:**
- Modify: `shaders/ground.gdshader` — `detail_fade_near` / `detail_fade_far` defaults (~line 94); confirm the `mix(texture(...), tex_mean(...), dis)` albedo lines are active in the mode-0 path.

**Interfaces:**
- Consumes: `tex_mean`, `texr_mean`, `detail_dissolve`, `detail_fade_on` (already present).

- [ ] **Step 1: Set the detail band to clear the speckle near→far**

In `shaders/ground.gdshader`, set the fade band (start close enough that the near speckle also dissolves, finish before the far moiré-resonance distance):

```glsl
uniform float detail_fade_near = 250.0;
uniform float detail_fade_far  = 1400.0;
```

- [ ] **Step 2: Confirm the mode-0 albedo uses the faded samples**

Verify the live (mode 0) albedo composite uses `a0..a4` built with `mix(texture(matX_alb, uv).rgb, tex_mean(matX_alb), dis)` (a3 via `tri_sample(..., dis)`), and `dis = detail_dissolve(dist) * detail_fade_on`. No code change if already so; otherwise restore those lines.

- [ ] **Step 3: Launch eye-gate, step to mode 2 (green) and mode 0**

```bash
powershell -Command "Get-Process | ? { \$_.ProcessName -like '*Godot*' } | Stop-Process -Force -EA SilentlyContinue"
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" \
  --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- \
  --cdlod=1 --clouds=0 --cam=0,900,0,-35,0 > /c/tmp/wg16shots/t3.txt 2>&1 &
```
Expected (USER eye-gate): mode 2 (green) rings GONE; press **G** to A/B (off should bring speckle back). Tune `detail_fade_near/far` if a residual band remains.

- [ ] **Step 4: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "Terrain AA: tune albedo detail-fade band to fully clear speckle moire"
```

---

### Task 4: Integrate — make the LIVE (mode 0) render carry both fixes + perturbation roll-off

Wire the live render so the default look (mode 0) gets proper tangent normals + Toksvig + faded albedo, with the perturbation strength also rolling off on the shared band. This is the actual shipping path (modes 1-4 are only eye-gate aids).

**Files:**
- Modify: `shaders/ground.gdshader` — the mode-0 block of the diag selector: use `apply_nrm_tangent` for the live normal (currently mode 0 sets `NORMAL = v_normal` with perturbation OFF), scaled by `nfade`, plus `rgh_aa` and faded albedo.

**Interfaces:**
- Consumes: `apply_nrm_tangent` (T1), `toksvig_*`/`rgh_aa` (T2), `dis`/faded albedo (T3).

- [ ] **Step 1: Build the live normal with rolled-off perturbation**

In the mode-0 branch, replace `NORMAL = normalize(v_normal);` so the live render uses the proper tangent normal faded by the shared band (full near, geometric far):

```glsl
if (dm == 0) {
    float nfade = (1.0 - dis);          // 1 near .. 0 far (dis already gated by detail_fade_on)
    vec3 lnrm = v_normal;
    lnrm = apply_nrm_tangent(lnrm, mat1_nrm, uv, (w1 + w2) / sum * nfade);
    lnrm = apply_nrm_tangent(lnrm, mat0_nrm, uv, w0 / sum * nfade);
    ALBEDO = alb;                        // alb already faded to mean by `dis` (Task 3)
    ROUGHNESS = rgh_aa;                  // Toksvig floor (Task 2)
    NORMAL = normalize(lnrm);
}
```

- [ ] **Step 2: Launch the full eye-gate (mode 0, the real look) — fly near→far**

```bash
powershell -Command "Get-Process | ? { \$_.ProcessName -like '*Godot*' } | Stop-Process -Force -EA SilentlyContinue"
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" \
  --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- \
  --cdlod=1 --clouds=0 --cam=0,900,0,-35,0 > /c/tmp/wg16shots/t4.txt 2>&1 &
```
Expected (USER eye-gate): mode 0 (default look) ring-free near→far; near-ground retains texture + relief; **G** A/Bs the whole fade. USER confirms before proceeding.

- [ ] **Step 3: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "Terrain AA: live render uses tangent normals + Toksvig + faded albedo (rings gone)"
```

---

### Task 5: Regression guards + perf check + cleanup

Confirm the vertex path is untouched (guards pass) and perf is within budget, then decide whether to keep the H-stepper/G-toggle in.

**Files:**
- Modify: `shaders/ground.gdshader` (only if a residual mip-band needs the optional explicit-LOD — likely none).

- [ ] **Step 1: Run the terrain guards (vertex path must be unaffected)**

```bash
GODOT="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe"
for chk in morphcheck stitchcheck streamcheck fieldcheck; do
  "$GODOT" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --$chk > /c/tmp/wg16shots/guard_$chk.txt 2>&1
  echo "== $chk =="; grep -iE "PASS|FAIL" /c/tmp/wg16shots/guard_$chk.txt | tail -3
done
```
Expected: each prints PASS.

- [ ] **Step 2: Perf check (frame time within budget)**

```bash
"$GODOT" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- \
  --cdlod=1 --clouds=0 --cam=0,900,0,-35,0 --profile=4 --profmove > /c/tmp/wg16shots/perf.txt 2>&1
grep -iE "PROFILE|fps|ms" /c/tmp/wg16shots/perf.txt | tail -10
```
Expected: frame time not materially worse than pre-change (extra cost < ~0.1 ms); USER confirms acceptable.

- [ ] **Step 3: Keep the debug aids (decision)**

Leave the H diag-stepper + colored modes + G toggle in `ground.gdshader` / `TerrainLabUI.Process.cs` as gated debug aids (default `diag_mode=0`, `detail_fade_on=1` → zero effect on the shipping look). They cost nothing at mode 0 and are the trustworthy eye-gate for any future surfacing work. (If the USER wants them stripped, remove the mode 1-4 branches and the H key, keep mode 0 inline.)

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "Terrain AA: guards pass + perf within budget; keep live eye-gate aids"
```

---

## Self-Review

**Spec coverage:**
- Normal channel — proper tangents → Task 1; Toksvig → Task 2; perturbation roll-off → Task 4. ✓
- Albedo channel — distance detail-fade to mean → Task 3. ✓
- Shared detail band (one seam) → `dis`/`detail_fade` used by both, set in Task 3, consumed in Tasks 2/4. ✓
- Eye-gate via H-stepper/G-toggle → every task's verify step; kept in Task 5. ✓
- No regression (guards) + perf → Task 5. ✓
- No asset reimport, no new C# → honored (debug toggles already built). ✓
- Out-of-scope (explicit mip-LOD, offline bake, ao/larger library) → not included; explicit-LOD only as a Task 5 contingency. ✓

**Placeholder scan:** No TBD/TODO; every code step shows the actual GLSL; commands have expected output. The only conditional is Task 5 Step 1 contingency (explicit-LOD) which is explicitly "likely none."

**Type consistency:** `apply_nrm_tangent` (T1) used in T2/T4; `toksvig_gloss`/`toksvig_rough_floor`/`rgh_aa` (T2) used in T4; `dis`/`tex_mean`/`detail_dissolve`/`detail_fade_on` consistent with the current shader. Mode numbering (0 full,1 red,2 green,3 blue,4 yellow) consistent across tasks and matches the live `diag_mode`.

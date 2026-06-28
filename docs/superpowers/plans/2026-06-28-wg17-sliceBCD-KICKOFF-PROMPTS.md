# WG17 Sky Stack B/C/D — New-Chat Kickoff Prompts

Three slices, built in order **B → C → D** (each reads from the one before; each degrades gracefully if an
upstream slice isn't merged). Run each in its own fresh chat (or sequentially in one). Paste the matching block.

**Godot 4.6 binary (this machine):**
`C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`

**Shared conventions (in every plan's Global Constraints, but they bite):**
- `dotnet build Terrainengine10k.csproj` after EVERY .cs edit — Godot does NOT rebuild C# on launch.
- Launch with absolute `--path C:/Wg16/WG17/terrainengine-10k`; CLI flags need a bare `--` separator; compute runs WINDOWED.
- GPU pattern: `CallOnRenderThread` + `Texture2Drd`/`Texture3Drd` **assigned ONCE** (RID never reassigned per frame — guards Godot bug #118292); RenderingDevice quarantined to the compute classes; NOT a CompositorEffect.
- One-way data flow via feeds: Lighting `ILuminaryFeed` → B + C; Atmosphere `IAtmosphereFeed` → C; Clouds `Overcast` (one scalar) → Lighting composer. Verify the REAL feed signatures against the merged Slice A/B files in `src/lighting/` and `src/atmosphere/`; if a slice isn't merged yet, stub with a reconcile TODO.

---

## Slice B — Atmosphere (REWRITE, not port)

```
We're migrating my Godot terrain engine into the WG17 repo (C:\Wg16\WG17\terrainengine-10k), Godot 4.6/C#.
This is SLICE B: ATMOSPHERE — physical sky scattering. IMPORTANT: this is a REWRITE from the Hillaire model,
NOT a port. WG16's atmosphere was never properly validated and its AT-2 (aerial perspective) was visibly
broken — a full-screen blue band/filter. We rebuild AT-1/2/3 fresh from the model, referencing WG16 only for
GPU plumbing.

Read in order:
1. Spec:   C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceB-atmosphere-design.md
2. Plan 1 (LUT core AT-1 + cloud-light AT-3):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-sliceB-atmosphere-plan1-luts.md
3. Plan 2 (aerial AT-2 + integration + the blue-band gates):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-sliceB-atmosphere-plan2-aerial.md

Hillaire model refs: https://sebh.github.io/publications/egsr2020.pdf and
https://github.com/JolifantoBambla/webgpu-sky-atmosphere (4 LUTs; aerial = 64x64x32 froxel).
WG16 GPU plumbing only: scripts/lab/AtmosphereCompute.cs, shaders/atmosphere_*.glsl.

Execute Plan 1 then Plan 2 task-by-task via superpowers:subagent-driven-development.

THE critical correctness rule (the whole reason this is a rewrite): aerial perspective applies to SCENE
GEOMETRY ONLY, gated by scene depth; the SKY/background is EXCLUDED (early-out on background depth). Applying
aerial to the whole frame = the WG16 blue band. Plan 2 has an AerialDepthCheck + eye-gate that specifically
assert sky-untinted + depth-graded — those must pass.

My eye-gate (I call it): you launch, I look. The B gate: physical sky across a day cycle, distant terrain
hazes but NEAR is clear, and NO flat blue band over the sky. Plus --atmoscheck and --aerialcheck print PASS.
Record a frame-ms number, then move on.

Confirm you've read the spec + 2 plans, then show me your plan for Plan 1 Task 1 before executing.
```

---

## Slice C — Clouds (port + fix 3 known bugs)

```
WG17 sky stack, SLICE C: CLOUDS — volumetric clouds. This is a faithful PORT of WG16's proven raymarch/noise
architecture, BUT we fix three known look-bugs as we go: (1) double-alpha composite, (2) sun-extinction
crushing direct light, (3) a dead noise octave. Do NOT attempt vertical-deck realism (explicit future work).

Read in order:
1. Spec: C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceC-clouds-design.md
2. Plan: C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-sliceC-clouds-plan.md

Source to port: scripts/lab/CloudVolume.cs, CloudNoiseCompute.cs, CloudParams/Layers/Weather/Presets.cs,
CloudLightCheck.cs, Std430Writer.cs; shaders/cloud_*.glsl/gdshader/gdshaderinc; data/cloud_*.json.
The 3 fixes are documented in docs/cloud-system-overview.md ("root-cause fixes") — port the FIXED versions and
VERIFY they hold (don't reintroduce them).

Reads from Slice A (ILuminaryFeed: sun/moon) + Slice B (IAtmosphereFeed: cloud ambient colors). If B isn't
merged, fall back to flat ambient (graceful). The ONLY back-edge is a single Overcast scalar → the composer.

Execute task-by-task via superpowers:subagent-driven-development.

My eye-gate: clouds over the lit+scattered sky; the 3 fixes visibly hold (soft edges, bright sun-facing sides,
fine detail); raising coverage dims the sun on terrain; yaw/pitch at a fixed pose doesn't change cloud
*lighting* (clouds parallax with camera MOVEMENT only — that's physical). --lightcheck prints PASS. Record ms.

Confirm you've read the spec + plan, then show me your plan for Task 1 before executing.
```

---

## Slice D — Godrays (port, carry the wash/ring fix)

```
WG17 sky stack, SLICE D: GODRAYS — screen-space crepuscular shafts. Smallest slice; a faithful PORT of WG16's
working screen-space radial-scatter god rays. CARRY the tangential high-pass intact (it killed the full-screen
wash + the bright ring around the sun — load-bearing). Do NOT rebuild as volumetric (the froxel attempts read
washy); do NOT bring back the deleted cloud-shadow-map mode. Occlusion comes from CLOUD LUMINANCE, not albedo.

Read in order:
1. Spec: C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceD-godrays-design.md
2. Plan: C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-sliceD-godrays-plan.md

Source to port: scripts/lab/GodRaysScreen.cs, shaders/godray_screen.gdshader. Quad at render_priority 127,
NOT a CompositorEffect. Reads from Slice C (cloud occlusion) + Slice A (sun dir). Without clouds it's a no-op
— build it last.

NOTE: godrays ARE view-dependent and that's CORRECT (screen-space camera effect) — unlike Lighting/Atmosphere/
Clouds-lighting. The HUD must NOT claim view-lock for this layer.

Execute task-by-task via superpowers:subagent-driven-development.

My eye-gate: clouds on, sun partway behind a cloud → crisp shafts from the sun's screen position; clear/
unocclude the sun → shafts vanish, NO wash, NO ring; sun off-screen → shafts fade smoothly, no pop. Record ms.

Confirm you've read the spec + plan, then show me your plan for Task 1 before executing.
```

---

## Notes for me (not the new chats)
- Order matters for the FULL look (B gives C its ambient; C gives D its occlusion), but each degrades
  gracefully, so they CAN be built independently if needed.
- Slice B is the real work (rewrite); C and D are ports-with-care. The blue-band gate (B Plan 2) is the
  highest-value check in the whole batch — it's the specific thing the user flagged.
- When each slice's chat starts, the FIRST thing it should do is verify the actual Slice A/B interface
  signatures in `src/lighting/`/`src/atmosphere/` (those slices may have evolved during execution).
- After D lands, the WG17 sky stack (A–D) is complete; next migration targets are Water and Material Board.
```

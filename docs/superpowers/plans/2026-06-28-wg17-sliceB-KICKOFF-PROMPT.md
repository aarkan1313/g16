# WG17 Slice B (Atmosphere) — New-Chat Kickoff Prompt

Run AFTER Slice A (Lighting) has landed in WG17. The sky stack order is **A: Lighting → B: Atmosphere →
C: Clouds → D: Godrays** — each reads from the one before it, so do them in order. Paste the block below
into a fresh Claude Code chat opened in `C:\Wg16\WG17\terrainengine-10k`.

---

```
We're migrating my Godot terrain engine module-by-module from WG16 into the fresh WG17 repo
(C:\Wg16\WG17\terrainengine-10k), staying on Godot 4.6/C#. This is SLICE B: ATMOSPHERE — second in the
sky stack (A: Lighting → B: Atmosphere → C: Clouds → D: Godrays). It's a focused PORT-CLEAN rewrite of
proven WG16 code: physical-atmosphere LUTs (Hillaire transmittance / multiscatter / sky-view) + screen-space
aerial perspective. Get it on screen reading from the lighting state, profile, then optimize — not a redesign.

PREREQUISITE: Slice A (Lighting) must be merged first — Atmosphere reads the sun/sky state THROUGH the
lighting seam (the composed LightingState / ILightingTarget output), never by reaching into scene nodes.
If Lighting isn't in yet, stop and do it first.

Before writing anything, AUDIT what WG16 actually has (don't trust this prompt's file list — verify):
  - Atmosphere core:  C:\Wg16\wg-16-project\scripts\**  (AtmosphereCompute.cs ~468 LOC, AerialPerspectiveV2.cs)
  - Atmosphere shaders: shaders\atmosphere_{transmittance,multiscatter,skyview,aerial_v2}.glsl,
                        shaders\aerial_screen_v2.gdshader
  - Config: data\ (any atmosphere/sky params JSON)
  Grep for the real files: `atmosphere`, `aerial`, `Hillaire`, `skyview`, `transmittance`, `multiscatter`.
  Also read the migration audit §4 (Clouds/Atmosphere/Godrays module) and §8 (cross-cutting risks):
  C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md

Build IN: C:\Wg16\WG17\terrainengine-10k\  (Te10k.* namespaces; src/atmosphere/ for the new code).

Process: write a short SPEC, then a PLAN (task-by-task with the exact files/commands), then execute with
superpowers:subagent-driven-development OR inline if I ask — same discipline either way. Confirm you've read
the WG16 source + audit and show me the spec before building.

Hard rules (they bite — same as the terrain + lighting slices):
- dotnet build Terrainengine10k.csproj after EVERY .cs edit before launching — Godot does NOT rebuild C#.
- Launch with absolute --path C:/Wg16/WG17/terrainengine-10k, never `.`. User CLI flags need a bare `--` separator.
- GPU compute (RenderingDevice / CallOnRenderThread / GLSL→SPIR-V) is Godot-native — this is COPY, not rewrite
  (staying on Godot 4.6). Shaders + any std430 push-constant layouts port BYTE-EXACT; guard with a self-check.
- LAYERING: keep RenderingDevice/RenderingServer/CallOnRenderThread quarantined to the atmosphere GPU classes
  (mirror how FieldCompute/ChunkFieldCache are quarantined in terrain). The compositor-effect / Texture2Drd RID
  race rules apply (see memory compute-to-material-callonrenderthread + std430-packing-helper).
- THE SEAM: Atmosphere consumes lighting via the lighting slice's one-way output (sun dir/color/intensity, sky
  tint, overcast) — inject it, don't GetNode or read /root/... paths. Atmosphere EMITS its sky/aerial result for
  Clouds (next slice) to read; design that hand-off as a clean interface now so Slice C needs no rework.
- Out of scope this slice: clouds (Slice C), godrays (Slice D), volumetric fog. Terrain's basic depth fog stays.
- Shadows are owned by the Lighting slice's ShadowRegistry — do NOT add shadow owners here.

Per-slice discipline (how I work): each slice ends with a QUICK eye-gate (you launch it self-driving — e.g.
--autotime or --fly — I look in motion) + a profile number recorded in the commit. The atmosphere LUTs are
amortized/cached, so measure the per-frame cost honestly (LUT rebuild only on sun-move, not every frame).
Don't tune past "looks right + isn't slower"; real optimization is the last task. Background-launched Godot
windows often don't grab keyboard focus — use a self-driving flag for any in-engine measurement, not manual input.

Godot 4.6 mono binary on this machine:
  C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe

Confirm you've read the WG16 atmosphere source + audit, then show me your SPEC before building anything.
```

---

## Notes for me (not the new chat)
- Order is A→B→C→D; each is its own spec→plan→kickoff. This is B. After it lands, write the C (Clouds) and
  D (Godrays) kickoffs the same way (they're heavier — CloudVolume ~775 LOC + cloud shaders; godrays is the
  screen-space radial-scatter `GodRaysScreen.cs`, NOT volumetric fog — see memory godray-emission-vs-albedo-rootcause).
- WATER/EROSION is a SEPARATE track (audit step 3), independent of the sky stack — it reuses IHeightSource with
  zero seam rework and can be done any time (its own kickoff). Cleanest module (21/24 files pure C#).
- Terrain base geometry is DONE (commits up to ~10356f7): streams/flies, snap-pop fixed, 6 checks pass, optimized.
- Relevant WG16 memories to recall when building B: volumetric-clouds-research, cloud-presence-research,
  cloud-shadow-dome-mismatch, compute-to-material-callonrenderthread, std430-packing-helper, headless-no-local-rendering-device.
```

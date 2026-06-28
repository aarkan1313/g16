# WG17 Terrain Slice — New-Chat Kickoff Prompt

Copy everything in the block below into a fresh Claude Code chat (open it in either repo; the plan references absolute paths so cwd doesn't matter, but `C:\Wg16\WG17\terrainengine-10k` is ideal).

---

```
We're migrating my Godot terrain engine module-by-module from WG16 into a fresh repo (WG17),
staying on Godot 4.6/C#. This first slice is the BASE GEOMETRY: procedural Field + CDLOD
infinite terrain. It's a focused PORT-CLEAN rewrite of proven WG16 code, not a from-scratch
redesign — get it on screen and flying first, profile, then optimize.

Read these first, in order:
1. Spec:  C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-terrain-slice-design.md
2. Plan:  C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-terrain-base-geometry.md
3. Audit (file manifest, LOC, shader/std430 contract):
          C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md

Source to port FROM:  C:\Wg16\wg-16-project\  (scripts\ and shaders\)
Target repo to build IN:  C:\Wg16\WG17\terrainengine-10k\  (bare Godot 4.6 project, no C# yet)

Execute the plan task-by-task using superpowers:subagent-driven-development (fresh subagent per
task, review between tasks). The plan's tasks already carry the steps, exact files, and commands.

Hard rules to keep front-of-mind (they're in the plan's Global Constraints, but they bite):
- Run `dotnet build` after EVERY .cs edit before launching — Godot does NOT rebuild C# on launch.
- Launch with an absolute `--path C:/Wg16/WG17/terrainengine-10k`, never `.`.
- User CLI flags after the scene need a bare `--` separator or they silently no-op.
- Bakes/self-checks need a WINDOWED run (local RenderingDevice), not --headless.
- Port the shaders (field_math.gdshaderinc, field_height.glsl, field_bake.glsl, ground.gdshader)
  BYTE-EXACT this slice — don't "clean up" shader math. The std430 ParamsBuf is exactly 128 bytes;
  FieldCheck guards that it survived the port.
- Keep the GPU binding (RenderingDevice/RenderingServer/CallOnRenderThread) ONLY in FieldCompute.cs
  and ChunkFieldCache.cs. CdlodQuadtree stays pure (math types only). Height consumers depend on
  IHeightSource, never FieldCompute concretely. Do NOT wrap Node3D/MeshInstance3D behind interfaces.

Per-slice discipline (this is how I want to work): each slice ends with a QUICK eye-gate (you
launch it, I look at it in motion) + a profile number recorded in the commit message — then we
move on. Don't tune a slice past "looks right + isn't slower"; real optimization is the last task.

The Godot 4.6 mono binary path on this machine: <FILL IN — find it, e.g. with `where godot` or
the launcher, before Task 2>.

Start with Task 1 (bootstrap the C# project). Confirm you've read the three docs and show me your
plan for Task 1 before executing.
```

---

## Notes for me (not for the new chat)

- The new chat should locate the Godot 4.6 mono binary early (Task 2 needs it to launch). If unknown,
  it can check the Godot launcher config or ask.
- Eye-gates are mine to call — the new chat launches, I look. It should pause for my "looks good" between slices.
- If a slice's eye-gate fails (flat/garbage terrain, cracks, pops, snap wobble), that's a STOP-and-debug,
  not a push-through — the spec §7 risks name the usual culprit per slice.
- After this slice lands, the same spec→plan→kickoff pattern repeats for Water → Clouds → Lighting
  (order per the audit). The IHeightSource seam means Water reuses the field with no rework.
```

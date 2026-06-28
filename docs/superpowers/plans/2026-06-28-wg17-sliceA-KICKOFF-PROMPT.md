# WG17 Slice A (Lighting) — New-Chat Kickoff Prompt

Copy the block below into a fresh Claude Code chat (ideally opened in `C:\Wg16\WG17\terrainengine-10k`).

---

```
We're migrating my Godot terrain engine module-by-module from WG16 into the fresh WG17 repo
(C:\Wg16\WG17\terrainengine-10k), staying on Godot 4.6/C#. This slice is SLICE A: the LIGHTING
FOUNDATION (Lighting + Sky-state + Luminary + a ShadowRegistry). It's first in the "sky stack"
(A: Lighting → B: Atmosphere → C: Clouds → D: Godrays) because everything reads from lighting,
and lighting correctness is the entire reason for the restart.

THE POINT of this slice: WG16's lighting got "weird" — turning the camera seemed to change
shading/shadows. Root causes were (1) too many uncoordinated shadow owners with hidden re-enable
paths, and (2) view-dependent SURFACING (normal-map fade, specular) misdiagnosed as shadow bugs.
WG17 fixes both: ONE writer of scene lighting via a one-way ILightingTarget; a ShadowRegistry that
asserts <=1 shadow owner per band (0 this slice); lighting that is view-independent by construction
(Compose() takes no camera input); and a diagnostic that labels view-dependent surfacing as owned by
a future material slice so the two can never be confused again.

Read these first, in order:
1. Spec:   C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceA-lighting-design.md
2. Plan 1 (data core + bootstrap):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-sliceA-lighting-plan1-datacore.md
3. Plan 2 (seams + composer + registry):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-sliceA-lighting-plan2-composer.md
4. Plan 3 (driver + scene + eye-gate):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-sliceA-lighting-plan3-driver.md

Source to port FROM:  C:\Wg16\wg-16-project\  (scripts\lab\Lighting*.cs, Luminary.cs, SkyPresets.cs,
LuminaryPresetCheck.cs ; data\*_presets.json, lighting_moods.json, luminaries.json)
Build IN:  C:\Wg16\WG17\terrainengine-10k\

Execute Plan 1, then Plan 2, then Plan 3, task-by-task, using superpowers:subagent-driven-development
(fresh subagent per task, review between tasks).

Hard rules (in the plans' Global Constraints, but they bite):
- `dotnet build Terrainengine10k.csproj` after EVERY .cs edit before launching — Godot does NOT rebuild C#.
- Launch with absolute `--path C:/Wg16/WG17/terrainengine-10k`, never `.`. CLI flags need a bare `--` separator.
- THE invariant: LightingComposer is the ONLY writer of Sun/Env, one-way through ILightingTarget; no GetNode,
  no /root/... paths, no read-back, no UI, no camera input in Compose(). ShadowRegistry asserts 0 owners.
- Fix-on-port: NO EMISSION ambient fill, NO hardcoded 0.12f ambient, NO legacy mood-dict->axes shim.
- The 4 physical-light cap (Godot LIGHT0..3) ports as-is — light COUNT was never the problem, the WRITERS were.

Per-slice discipline (how I work): the headline eye-gate is mine to call — you launch with `-- --autotime`,
then PAUSED at a fixed time you mouse-look yaw/pitch and I confirm the LIT WORLD doesn't change (only which
faces I see). Plus the three checks (--luminarycheck / --shadowcheck / --composercheck) must print PASS.
Record a frame-ms number. Then we move on.

If the terrain slice hasn't merged yet (no src/terrain/), Plan 3 Task 3 builds a placeholder lit scene
(plane + boxes) so lighting can be eye-gated standalone — lighting does NOT depend on terrain code.

Godot 4.6 mono binary path on this machine: <FILL IN — `where godot` or find Godot_v4.6*mono*win64.exe>.

Confirm you've read the spec + 3 plans, then show me your plan for Plan 1 Task 1 before executing.
```

---

## Notes for me (not the new chat)
- Three plan files, each focused (~250–400 lines): data-core, composer+registry, driver+eye-gate. Execute in order 1→2→3.
- The view-lock eye-gate (Plan 3 Task 5 Step 2) is THE gate for this slice — yaw/pitch at fixed time, lit world must not move.
- Lighting is independent of terrain; the placeholder scene (Plan 3 Task 3) unblocks it if the terrain chat is still mid-flight.
- After Slice A lands, repeat spec→plans→kickoff for Slice B (Atmosphere), then C (Clouds), then D (Godrays).
```

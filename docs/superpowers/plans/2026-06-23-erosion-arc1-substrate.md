# Erosion Arc 1 — Sim Core + Drainage Substrate (river fix) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the existing race-free pipe-model erosion sim form convincing DENDRITIC RIVERS by adding flow-accumulation + stream-power incision, and output the drainage substrate (flow_accum, channel_mask, water_level) the water arcs consume.

**Architecture:** Extend `shaders/erosion_sim.glsl` + `scripts/erosion/*` (no new system). Add an iterative, race-free flow-accumulation field A (downhill-weight compute → gather, mirroring the water flux→gather pattern); rewrite erode capacity to stream-power `∝ A^m · S^n`; derive channel_mask from A; add basin-fill to water_level. Judged live in the existing erosion_lab.

**Tech Stack:** Godot 4.6.2 mono (C#), RD-GLSL compute on a local RenderingDevice, windowed `--erosioncheck` + live eye-gate. NOT TDD — GPU/visual gates.

## Global Constraints

- **Race-freeness rule (the bug that already bit us):** no phase reads neighbor h/s/accum while writing that same buffer in the SAME dispatch. Neighbor-reading mutations = compute-flux/weight pass (read-only) + gather-apply pass (write own index only). See the kernel header comment + memory `erosion-e1-pipemodel-race`.
- **Bones untouched:** do NOT edit `field_math.gdshaderinc` / `field_height.glsl`. `--fieldcheck` (terrain_lab) stays `maxAbsDiff=0m`.
- **std430 sync:** any `ErosionParams` field add must keep `Pack()` byte layout matching the GLSL `Params` block exactly (int res slot 0, floats after, pad to 16-scalar/64-byte). Add new floats in the pad slots or extend both in lockstep.
- **Stale DLL / shader cache:** `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` after each `.cs` edit; clear `C:/Users/josep/AppData/Roaming/Godot/app_userdata/WG16 base field/shader_cache` if a `.glsl` edit seems ineffective.
- **Run:** windowed only (local RD). Console exe: `/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe`. Lab scene: `scenes/erosion_lab.tscn`. Kill stray Godot first.
- **Gate:** mechanical `--erosioncheck` (finite + roughness<0.02 + flow_accum has dynamic range) then USER eye-gate — dendritic rivers in motion. STOP if rivers can't be reached after fair effort (graveyard arc).

## File Structure

- **Modify `shaders/erosion_sim.glsl`** — add accumulation passes + stream-power incision + channel_mask + basin water_level. (Currently phases 1-7; this adds accumulation phases + new output writes.)
- **Modify `scripts/erosion/ErosionSim.cs`** — add `accum`, `accum2`, `wlevel`, `cmask` SSBOs + bindings; extend Step phase order; expose via ReadDebug.
- **Modify `scripts/erosion/ErosionParams.cs`** — add `StreamM`, `StreamN`, `AccumRate`, `ChannelThreshold`; keep Pack() in sync (extend the struct + GLSL Params together).
- **Modify `scripts/erosion/ErosionLab.cs`** — extend D-key debug cycle to flow_accum (3) + channel_mask (4).

---

### Task 1: Flow-accumulation field (race-free iterative gather)

**Files:** Modify `shaders/erosion_sim.glsl`, `scripts/erosion/ErosionSim.cs`, `scripts/erosion/ErosionParams.cs`

**Interfaces:** Produces an `accum` SSBO (binding 9) holding per-cell upstream drainage A, converging over steps; new phases ACCUM_WEIGHT + ACCUM_GATHER; `ErosionParams.AccumRate`.

- [ ] Add `accum` (binding 9) + `accum2` (binding 10, double-buffer) SSBOs to ErosionSim.Alloc + ClearDynamics + Dispose + the binding array; add to GLSL as `layout(...binding=9) buffer Accum {float a[];}` and `binding=10 Accum2 {float a2[];}`.
- [ ] Add accumulation phases to the kernel. ACCUM_WEIGHT: each cell, read neighbor heights, compute the fraction of its A it sends to each LOWER neighbor (steepest-descent or D∞-style weights), write own outflow weights to a buffer (reuse `tflux`-style vec4 or a dedicated `aflux`). ACCUM_GATHER: `a2[i] = 1.0 + (sum of neighbors' inflow weighted by their A)`; then a swap phase `a[i]=a2[i]`. (1.0 = the cell's own rain contribution; iterating propagates upstream area downhill. Race-free: weight pass read-only on a/h, gather writes own a2.)
- [ ] Add `AccumRate`/relaxation if needed for stability; ErosionParams + Pack() + GLSL Params extended in lockstep (use pad slots `_p0.._p2`, add more scalars if needed — keep 16-aligned).
- [ ] Insert the accumulation phases into `Step` (after WATER, before ERODE, so incision reads current A).
- [ ] Build; `--erosioncheck` still PASS (finite). Add to the check: print `flow_accum` max/mean ratio (channels should be >> mean → dynamic range exists). Commit: `erosion arc1 T1: race-free flow-accumulation field`.

### Task 2: Stream-power incision (rivers carve where flow concentrates)

**Files:** Modify `shaders/erosion_sim.glsl`, `scripts/erosion/ErosionParams.cs`

**Interfaces:** Consumes `accum` from T1; `ErosionParams.StreamM`, `StreamN`.

- [ ] Replace the erode capacity (phase 3) `cap = capacity·slope·speed·clamp(w·40,…)` with stream-power: `cap = capacity · pow(accum[i]·cell_area, StreamM) · pow(slope, StreamN)`. Keep the `MaxErode` per-step cap (anti-overshoot) + the deposit branch. (Velocity/water can stay as a secondary factor or be dropped — A·S is the driver.)
- [ ] Add `StreamM` (≈0.5), `StreamN` (≈1.0) to ErosionParams + Pack() + GLSL Params (lockstep).
- [ ] Build; clear shader cache; `--erosioncheck` PASS (finite, roughness<0.02). Commit: `erosion arc1 T2: stream-power incision on accumulated drainage`.

### Task 3: Channel mask + basin water level (the rest of the substrate)

**Files:** Modify `shaders/erosion_sim.glsl`, `scripts/erosion/ErosionSim.cs`, `scripts/erosion/ErosionParams.cs`

**Interfaces:** Produces `cmask` (binding 11), `wlevel` (binding 12); `ErosionParams.ChannelThreshold`.

- [ ] Add `cmask` + `wlevel` SSBOs (bindings 11/12) to ErosionSim + GLSL.
- [ ] Channel mask phase: `cmask[i] = accum[i] > ChannelThreshold ? clamp(log(accum)/k,0,1) : 0` (a soft stream-order proxy; own-index write, race-free).
- [ ] Basin water level: a simple fill — `wlevel[i] = max(h[i], min over a few iterations of lowest-escape)`; for phase 1 a cheap version is fine (standing water where local depression + water depth above a threshold). Keep it simple; full priority-flood is optional/later. Own-index gather, race-free.
- [ ] `ChannelThreshold` to ErosionParams + Pack() + GLSL (lockstep).
- [ ] Build; `--erosioncheck` PASS. Commit: `erosion arc1 T3: channel mask + basin water level (substrate complete)`.

### Task 4: Lab debug views + eye-gate + tune

**Files:** Modify `scripts/erosion/ErosionLab.cs`; tune `ErosionParams` defaults

**Interfaces:** Consumes all substrate fields via ReadDebug.

- [ ] Extend ErosionSim.ReadDebug to return accum (3), cmask (4), wlevel (5); extend ErosionLab's D-key cycle + HUD label to name them.
- [ ] Build; run `--erosioncheck`; confirm field guard (`--fieldcheck` 0m in terrain_lab).
- [ ] USER EYE-GATE: launch `scenes/erosion_lab.tscn`, run it, cycle D to flow_accum + channel_mask. Confirm: dendritic branching network in the accum/channel views; valleys carve along them; drains downhill; converges; tunable. Iterate StreamM/StreamN/AccumRate/rain/evaporate/ChannelThreshold defaults until the user judges rivers GREAT — or STOP and surface if unreachable.
- [ ] On PASS: commit `erosion arc1 T4: substrate debug views + tuned rivers (eye-gate passed)`; update roadmap (Arc 1 phase 1 done).

## Self-Review

- **Spec coverage:** flow-accumulation (T1) ✓, stream-power incision (T2) ✓, channel_mask + water_level (T3) ✓, substrate debug + eye-gate (T4) ✓, race-freeness preserved (constraints + each pass split) ✓, bones untouched (constraints) ✓, STOP criterion (T4) ✓.
- **Placeholders:** the basin water-level "simple fill" is intentionally scoped-cheap for phase 1 with a concrete starting formula + "full priority-flood later" — a bounded contingency, not a vague TODO.
- **Consistency:** new buffers bindings 9-12 follow the existing 0-8; ErosionParams/GLSL Params kept in lockstep at each add; ReadDebug channel numbers consistent (0 water,1 sediment,2 velocity,3 accum,4 cmask,5 wlevel).

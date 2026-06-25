# Per-chunk field cache (GPU-compute) — Implementation Plan

> **For agentic workers:** implement task-by-task. No unit tests (Godot lab) — verification is build-green + the
> mechanical `--*check` gates staying PASS + a drift-free in-motion EYE-GATE (`--fieldcache=1` vs `=0` must look
> IDENTICAL) + a re-profile of the saving. Steps use checkbox (`- [ ]`). Commit per task on `experiment/presentation`.

**Goal:** Bake each CDLOD chunk's height+normal once on birth via GPU compute into a `Texture2DArray`, sample it
in the vertex shader instead of evaluating the field 5×/vertex/frame. Quality-identical. Removes most of the
~3.7 ms base floor.

**Spec:** `docs/superpowers/specs/2026-06-24-per-chunk-field-cache-design.md` (read first).

## Global Constraints
- **CDLOD is default-on.** Test with the real profiler: `--cdlod=1 --profmove --profile=8 --profspeed=800`.
- **Gates MUST stay PASS** after the wiring tasks: `--fieldcheck --popcheck --morphcheck --stitchcheck
  --streamcheck --snapdiff`. The cache is quality-identical, so any gate FAIL = a bake/sampling bug.
- **Reversible:** everything behind `CdlodTerrain.FieldCache` (default true) + `--fieldcache=0` → today's live path.
- **Render-thread RD only** (local RD NullRefs headless). Bake math uses `FieldCompute.PackParamsBytes` (byte-
  identical to the live field → `--fieldcheck` safe). Do NOT touch the water chat's files.
- **Don't break the AABB tighten** — the cache SUBSUMES it (min/max from the same baked grid).

---

### Task 1: SPIKE — prove the RD texture-array → material `sampler2DArray` binding

**Files:** Create `shaders/field_bake.glsl`; modify `scripts/lab/ChunkAabbProvider.cs` is NOT touched — create
`scripts/lab/ChunkFieldCache.cs` (skeleton); temporary test hook in `TerrainLabUI.Cli.cs` (`--fieldcachespike`).

**Interfaces produced:** confirmation that a persistent RD `Texture2DArray` (RDTextureFormat TextureType2DArray,
R32G32B32A32Sfloat, N layers, storage+sampling usage) written via `TextureUpdate(rid, layer, bytes)` and bound to
a material via `Texture2DArrayRd { TextureRdRid = rid }` samples correctly as `sampler2DArray`.

- [ ] **Step 1:** Write `shaders/field_bake.glsl` (RD-GLSL compute, `// @@INCLUDE field_math` splice like
  `field_height.glsl`). Local size 8×8. Inputs: params buffer (origin, spacing, res via `PackParamsBytes`) + a
  storage buffer output `float out_data[]` sized `res*res*4` (vec4 per texel). Per invocation `(gx,gy)<res`:
  `vec2 w = origin + vec2(gx,gy)*spacing; float h = field_height(w);` normal via fixed-step central diff:
  `float ns = max(analytic_spacing,1.0); vec3 n = normalize(vec3(field_height(w-vec2(ns,0))-field_height(w+vec2(ns,0)), 2.0*ns, field_height(w-vec2(0,ns))-field_height(w+vec2(0,ns))));`
  write `out_data[(gy*res+gx)*4+0..3] = h, n.x, n.y, n.z`. (5 field evals/texel, ONCE on birth.)
- [ ] **Step 2:** In `ChunkFieldCache.cs`, mirror `ChunkAabbProvider`'s render-thread setup (`EnsureRt` compiling
  `field_bake.glsl`). Add a persistent array texture created once on the render thread:
  `var f = new RDTextureFormat { Width=(uint)Side, Height=(uint)Side, ArrayLayers=(uint)Slots, Format=R32G32B32A32Sfloat, TextureType=TextureType2DArray, UsageBits=CanUpdateBit|SamplingBit };`
  `_arrayRid = _rd.TextureCreate(f, new RDTextureView());` Expose `Rid ArrayRid => _arrayRid;`.
- [ ] **Step 3:** Spike-bake ONE layer: dispatch `field_bake.glsl` for a known chunk footprint, `BufferGetData`,
  `_rd.TextureUpdate(_arrayRid, 0, bytes)`. (Side = GridN+2 = 67; pad handled in Task 2.)
- [ ] **Step 4:** Bind: a `Texture2DArrayRd _tex = new(); _tex.TextureRdRid = _arrayRid;` assigned ONCE (race-safe,
  per the Texture2Drd memory) and `mat.SetShaderParameter("chunk_cache", _tex)`. Add a throwaway debug branch in
  `ground.gdshader` fragment guarded by a `cache_spike` uniform: `ALBEDO = texture(chunk_cache, vec3(UV,0.0)).rrr * 0.001;`
  (just prove it samples without error — a height-tinted ground).
- [ ] **Step 5 (gate):** `dotnet build` (0 err) → headless `--import` (shader compiles) → windowed
  `--fieldcachespike`: the scene renders, no RD errors in the log, the debug branch shows height variation. If
  `Texture2DArrayRd` doesn't exist / won't bind, FALL BACK to a per-slice `Texture2Drd[]` array of 2D textures
  indexed in the shader by a flat uniform array, OR a single tall atlas `Texture2Drd` (Side × Side*Slots) sampled
  by `vec2(u, (slot+v)/Slots)`. Record which binding works. **This unblocks everything; do not proceed until a
  sampling path is proven.** Commit.

### Task 2: `ChunkFieldCache` — full async per-chunk bake + slot manager + AABB subsume

**Files:** `scripts/lab/ChunkFieldCache.cs` (complete it).

**Interfaces produced:** `Request(long key, int slot, Vector2 originXZ, float size)` (queue a bake for a chunk at a
texture-array slot); `bool TryTake(out long key, out int slot, out float lo, out float hi)` (drained game-side:
the bake landed → slot is `cache_ready`, plus the AABB min/max from the same grid); `void Pump()`; `Rid ArrayRid`;
`Texture2DArrayRd Tex`; `int Slots`; `int Side` (=GridN+2); `void Prewarm()`; `void Dispose()`.

- [ ] **Step 1:** Bake covers the chunk footprint PLUS a 1-texel border each side at the chunk's vertex spacing so
  the (GridN+2)² grid's interior (GridN+1)² maps to vertex UVs `[0,1]` with a half-texel inset usable for the
  geomorph coarse-texel and edge normals. Concretely: `spacing = size / (GridN-1)` (vertex spacing);
  `origin = originXZ - spacing` (start one texel before the chunk min corner); `res = GridN+2`.
- [ ] **Step 2:** `BakeChunk`: dispatch `field_bake.glsl` over res×res, `BufferGetData`, compute `lo/hi` from the
  height channel (every 4th float) → the AABB, then `_rd.TextureUpdate(_arrayRid, slot, bytes)` (one slice). Push
  `Done { key, slot, lo, hi }`. Mirror `ChunkAabbProvider.HeightRange` resource handling (transient buffers; a
  persistent ring is a later optimization — out of scope here).
- [ ] **Step 3:** `MaxRequestsPerFrame` defaults LOWER than the AABB probe (start 4) — the (GridN+2)² dispatch is
  ~86× heavier; the re-profile (Task 5) tunes it. Throttle + dedup keyed by `key` exactly like the provider.
- [ ] **Step 4 (gate):** build green; `--import` green. No live wiring yet (next task), so just compile + a unit
  call from the spike hook baking 2-3 chunks into distinct slots, log `lo/hi` sane (within −180..639). Commit.

### Task 3: Wire into `CdlodTerrain` (slots, requests, landing, per-instance push, retire)

**Files:** `scripts/lab/CdlodTerrain.cs`, `scripts/lab/TerrainLab.cs` (passthrough), `scripts/lab/TerrainLabUI.Cli.cs`
(`--fieldcache=`).

**Interfaces produced:** `CdlodTerrain.FieldCache` (bool, default true); each `ChunkSlot` gains `int CacheSlot`
(texture-array layer, −1 = none) + `bool CacheReady`.

- [ ] **Step 1:** Construct `ChunkFieldCache` in `Setup` (Slots = a cap ≥ steady-state active, e.g. 768; Side =
  GridN+2). `Prewarm()` alongside the AABB provider. When `FieldCache` is on, the cache SUBSUMES the AABB probe —
  do NOT also call `_aabbProvider.Request` (the cache's `TryTake` carries the AABB lo/hi).
- [ ] **Step 2:** Slot pool: a free-list `Stack<int>` of layer indices `0..Slots-1`. On birth (`AcquireSlot`
  region), pop a free layer → `slot.CacheSlot`; `slot.CacheReady=false`; `_fieldCache.Request(key, slot.CacheSlot,
  c.OriginXZ, c.Size)`. On retire, push `slot.CacheSlot` back to the free-list (its bake result is now stale-safe
  — a future bake overwrites it before `CacheReady` is re-set).
- [ ] **Step 3:** Drain in `Tick` (replacing/with `DrainTightened`): `while (_fieldCache.TryTake(out key, out
  islot, out lo, out hi))` → find the live slot for `key`, set `CacheReady=true`, apply the tight AABB (reuse the
  existing tighten-apply path with lo/hi). Push per-instance: in `ApplyChunk`, `mi.SetInstanceShaderParameter
  ("chunk_slot", (float)slot.CacheSlot)` and `("cache_ready", slot.CacheReady ? 1.0f : 0.0f)` (re-push when
  CacheReady flips — add to the `snapped||isNew` apply + a CacheReady-changed check).
- [ ] **Step 4:** Bind the array texture to the material once the RID is live (deferred like the cloud sky):
  `_mat.SetShaderParameter("chunk_cache", _fieldCache.Tex)`. Push `Side`/`GridN` as uniforms for the UV math.
- [ ] **Step 5:** `FieldCache=false` path: skip requests, never set CacheReady → the shader's live path runs
  (Task 4). `--fieldcache=0` CLI (default 1). `TerrainLab.SetFieldCache(bool)` passthrough.
- [ ] **Step 6 (gate):** build green; `--cdlod=1 --fieldcache=1` launches, no RD errors, terrain renders. Gates
  not yet meaningful (shader still live-path until Task 4 samples). Commit.

### Task 4: Vertex-shader dual-path (sample the cache, two-sample geomorph)

**Files:** `shaders/ground.gdshader`.

- [ ] **Step 1:** Add uniforms: `uniform sampler2DArray chunk_cache;` `instance uniform float chunk_slot;`
  `instance uniform float cache_ready;` `uniform float grid_n; uniform float cache_side;` (already have `grid_n`).
  UV mapping: a vertex at grid coord `u_morph∈[0,1]` maps to the baked texel with the 1-texel border:
  `vec3 cuv(vec2 uv01, float slot){ float s = cache_side; vec2 t = (uv01*(grid_n-1.0) + 1.0 + 0.5)/s; return vec3(t, slot); }`
  (the `+1` border offset, `+0.5` texel center).
- [ ] **Step 2:** In the `use_chunk` branch, BEFORE the 5 `analytic_h` calls, branch:
  `if (cache_ready > 0.5) { float hf = textureLod(chunk_cache, cuv(u_fine, chunk_slot),0.0).r; float hc =
  textureLod(chunk_cache, cuv(u_coarse, chunk_slot),0.0).r; h0 = mix(hf,hc,morphK) - carve_offset(wxz); vec3 nf =
  textureLod(chunk_cache, cuv(u_fine,chunk_slot),0.0).gba; vec3 nc = textureLod(chunk_cache,
  cuv(u_coarse,chunk_slot),0.0).gba; v_normal = normalize(mix(nf,nc,morphK)); }` ELSE the existing 5-eval live
  block (unchanged). Set `VERTEX.y=h0; NORMAL=v_normal; v_h=h0;` after the branch (shared).
  - NOTE: `u_fine`/`u_coarse` are the snapped grid UVs already computed (the morph endpoints); confirm their
    names in code and that they're in `[0,1]` chunk space. The two samples reproduce CDLOD geomorph exactly.
- [ ] **Step 3 (GATE — the critical one):** build → `--import` → run ALL gates with `--cdlod=1 --fieldcache=1`:
  `--fieldcheck --popcheck --morphcheck --stitchcheck --streamcheck --snapdiff` MUST all PASS. `--popcheck` (height
  + normal continuity across LOD) and `--morphcheck` (the two-sample blend) are the ones that catch a wrong UV /
  wrong coarse-texel. If `--popcheck` normal≠0: the cached normal UV or the border offset is off — fix `cuv`. If
  `--fieldcheck`≠0 m: the bake origin/spacing disagrees with the live sample — fix Task 2 Step 1. Commit only when
  ALL pass.
- [ ] **Step 4 (EYE-GATE):** `--profmove` fly with `--fieldcache=1` vs `=0` — must look IDENTICAL (drift-free: same
  field). A/B a frozen `--auto-shot` at a fixed `--cam` for each, diff. If identical → quality preserved. Commit.

### Task 5: Re-profile, tune the bake throttle, document

**Files:** `docs/performance.md`, `docs/DECISIONS.md`, `docs/HANDOFF.md`, memory.

- [ ] **Step 1:** Re-profile `--cdlod=1 --profmove --profile=8 --profspeed=800` with `--fieldcache=1` vs `=0`. Log
  avg/worst + `PROFILE-STREAM` births. Expect the base floor to drop ~2-3 ms. Watch the WORST (birth-burst render-
  thread bake spike).
- [ ] **Step 2:** If the worst-case spiked from the bake, lower `ChunkFieldCache.MaxRequestsPerFrame` (4→2) and
  re-profile — the live dual-path means a slower cache fill is invisible (no holes), so trade fill-rate for
  smoothness until the worst-case is acceptable. Record the chosen value.
- [ ] **Step 3:** Update `performance.md` (a new "field cache shipped" row in the ARC A-2 table with the measured
  saving), `DECISIONS.md` (the entry), `HANDOFF.md` §6, and the memory note. Commit.

## Self-review
- Coverage: spec's bake (T1/T2) + sample (T4) + wiring/async/dual-path (T3) + verify/profile (T5). ✓
- Type consistency: `chunk_slot`/`CacheSlot` (int layer), `cache_ready`/`CacheReady` (bool→float uniform),
  `Side=GridN+2`, `Request(key,slot,origin,size)`/`TryTake(key,slot,lo,hi)` match across T2→T3. ✓
- Gate discipline: T4 Step 3 (mechanical) + Step 4 (eye-gate) before claiming done; T1 proves the binding first. ✓
- Risk: the RD texture-array binding (T1 spike fallbacks) and the geomorph UV/border math (T4 popcheck) are the
  two real unknowns — both gated early.

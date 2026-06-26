Priority 0 — stop the regression first
1. Remove / disable the velocity-scaled birth burst

Problem:
Current code can scale births up to:

MaxChunkOpsCeil = 256
ChunkOpsPerSpeed = 0.01f

At high speed, that means you can birth way more chunks per frame than the cache can bake. Your cache bakes default to 16 per frame, so births can outrun bake readiness hard. Your own note says this is probably what caused the brown rectangle artifact.

Files:

scripts/lab/CdlodTerrain.cs

Change:

int effOps = MaxChunkOps;

Instead of:

int effOps = Mathf.Min(MaxChunkOpsCeil, MaxChunkOps + (int)(velXZ.Length() * ChunkOpsPerSpeed));

Keep these fields only as disabled diagnostics for later:

public int MaxChunkOpsCeil = 256;
public float ChunkOpsPerSpeed = 0.01f;

But do not use them in the default path.

Why:
The budget is supposed to prevent churn. Right now it can become a burst system and overload the cache/AABB pipeline.

2. The performant version
1. Keep Ring 8, but split it into “active ring” and “horizon ring”

Example:

public int ActiveRing = 4;
public int HorizonRing = 8;

Then:

0–4 rings:
  normal CDLOD

5–8 rings:
  coarse horizon only

The far horizon chunks should be 8192 m root chunks only, or maybe one level below root if absolutely needed. No deep recursion. No normal CDLOD splitting. No cache baking per new fine chunk.

Your quadtree already has the right idea: larger ring cells are farthest and coarsest.
But you need to enforce that far-ring cells stay coarse and cheap.

2. Far ring should not use the same birth budget as near terrain

This is the biggest design fix.

Near chunks and far chunks should not compete for MaxChunkOps.

Right now, a far chunk can consume the same birth pipeline as a near visible chunk. That is bad.

Do this instead:

Near birth budget:
  priority, camera-near, visible, gameplay important

Far birth budget:
  tiny background budget, lazy, no urgency

Example:

public int NearChunkOps = 24;
public int FarChunkOps = 4;

Far ring should fill gradually. If a far cell is missing for a few frames, fog/haze/clip can hide it. If a near child is missing, the player sees it immediately.

3. Do not field-cache the far ring

For Ring 8, I would not cache every far coarse chunk the same way. Your field cache is useful near the camera, but it is also where the brown rectangle/stale-slot failure came from when births burst faster than bake throughput. The current code has CacheSlots = 1280, says Ring 8 active chunks are around 900, and bakes chunk height/normal into texture-array layers.

For far horizon chunks:

cache_ready = 0
live analytic path is okay
or use a separate very low-res far cache

Why? Because far chunks are few vertices and coarse. A live analytic 65×65 root chunk is not the thing killing you. The expensive part is churn, cache management, and server updates.

So:

Near/fine chunks:
  use field cache

Far/coarse horizon chunks:
  no field cache, no cache slot, no delayed bake
4. Do not AABB-tighten the far ring

AABB tightening is for shadow/culling precision near the player. The far ring should use generous AABBs and mostly avoid shadow casting.

Current AABB work uses an async render-thread provider, but it still has GPU readback behavior in ChunkAabbProvider.

For far horizon chunks:

TightenAabb = false
CastShadow = off
generous AABB only

That removes a whole source of culling weirdness and render-thread work.

5. Far ring should be persistent, not constantly born/retired

The ideal Ring 8 far system is a rolling grid.

For Ring 8, root cells are basically:

17 × 17 = 289 root cells

That is not a scary number.

Instead of repeatedly reconciling far chunks through the same dictionary birth/death path, keep a persistent pool of far root-cell instances:

FarHorizonGrid:
  17x17 fixed MeshInstance3D slots
  each slot maps to one root cell
  when camera crosses a root cell boundary:
    recycle only the entering row/column

That means:

No per-frame far-ring birth churn.
No far cache layer churn.
No far AABB probe churn.
No far shadow churn.

At most, when crossing an 8192 m boundary, you update one row/column of far cells.

6. Render far ring as horizon terrain, not gameplay terrain

Far Ring 8 is visual distance. It does not need:

collision
water detail
flora
fine material detail
high-res normals
dynamic shadows
near-distance chunk logic

It needs:

silhouette
macro color
aerial haze
maybe horizon self-shadow / macro lighting

So yes, Ring 8 can be performant if it is treated like a horizon/visual shell, not like normal streamed gameplay terrain.

The actual target architecture I’d use
Ring 0–4: Active CDLOD
  - normal chunk selection
  - field cache allowed
  - AABB tighten allowed near camera
  - shadows allowed
  - priority birth budget
  - geomorph/stitching

Ring 5–8: Horizon CDLOD / Far Shell
  - root chunks only, or max one split
  - persistent rolling grid
  - no field cache slots
  - no AABB tighten
  - no shadow casting
  - no collision
  - low material detail
  - updated only on root-cell crossing

That keeps your desired view distance without making every far chunk participate in the expensive active terrain system.

So should you keep Ring 8?

Yes — but I would not make the current Ring 8 path the default yet.

I’d do this:

Step 1:
  Restore stable active terrain at Ring 4.

Step 2:
  Add Ring 8 as far horizon shell only.

Step 3:
  Keep the far clip / fog / aerial range matched to Ring 8.

Step 4:
  Only then make “8-ring view distance” the default.

The other chat was not wrong that Ring 8 caused regression in the current implementation. But that does not mean Ring 8 is the wrong long-term target. It means Ring 8 should not be implemented as “just make the active streaming window bigger.”

3. Revert FlyCamera.InitialSpeed to sane value

Problem:
The note says FlyCamera.InitialSpeed = 5000 was a review aid and should be reverted to 120 before finalizing.

Files:

scripts/workbench/FlyCamera.cs
scenes/terrain_lab.tscn or scenes/review.tscn if overridden in scene

Change:

[Export] public float InitialSpeed = 120f;

or at most:

[Export] public float InitialSpeed = 200f;

Why:
Testing streaming at insane fly speed can make every bug look worse. You still need a boost mode, but normal review should not start at 5000 m/s.

Priority 1 — chunk pop / disappear / brown rectangle fixes
4. Delay cache-layer reuse

Problem:
This is the big one. The cache bake is fire-and-forget. Your code even comments that the bake writes into a texture-array layer asynchronously, and CacheReadyDelay exists because sampling too early can show previous data. But retired cache slots are returned immediately to _freeLayers. That means a stale GPU write can still hit a layer after that layer was reused by a different chunk. The field cache uses texture-array layers and fire-and-forget image writes, so this is a real stale-slot hazard.

Files:

scripts/lab/CdlodTerrain.cs

Add fields:

private const int CacheLayerFreeDelay = 8;
private readonly List<(int slot, int releaseFrame)> _cacheLayersPendingFree = new();

Add helper:

private void ReleaseDelayedCacheLayers()
{
    for (int i = _cacheLayersPendingFree.Count - 1; i >= 0; i--)
    {
        var item = _cacheLayersPendingFree[i];

        if (_frame < item.releaseFrame)
        {
            continue;
        }

        _freeLayers.Push(item.slot);
        _cacheLayersPendingFree.RemoveAt(i);
    }
}

Call after _frame++:

_frame++;
ReleaseDelayedCacheLayers();

Replace immediate free:

if (dead.CacheSlot >= 0)
{
    _freeLayers.Push(dead.CacheSlot);
    dead.CacheSlot = -1;
}

with:

if (dead.CacheSlot >= 0)
{
    _cacheLayersPendingFree.Add((dead.CacheSlot, _frame + CacheLayerFreeDelay));
    dead.CacheSlot = -1;
}

Why:
This protects against old GPU work writing into a newly reused cache layer.

5. Mark returned cache layers as not reusable until no pending landing references them

Problem:
The delay above is the minimum fix. Better fix: also clear any _cachePending entries that reference a retired slot/key. Right now _cachePending resolves later and checks whether the active chunk still owns the slot, but it does not prevent layer reuse. The stale-landing guard is good, but it only guards the ready flip, not the GPU write itself.

Files:

scripts/lab/CdlodTerrain.cs

Add during retirement, after delaying the slot:

for (int p = _cachePending.Count - 1; p >= 0; p--)
{
    if (_cachePending[p].key == _scratchDead[i])
    {
        _cachePending.RemoveAt(p);
    }
}

Why:
This reduces stale bookkeeping and avoids delayed logic touching dead chunk state.

6. Sort missing chunk births by importance before spending the budget

Problem:
Right now you walk leaves in selection order and birth until births >= effOps. The code comments say births are budget-capped and skipped chunks appear in later frames, but nothing guarantees that the skipped chunks are far or unimportant.

Files:

scripts/lab/CdlodTerrain.cs

Better structure:

private readonly List<CdlodChunk> _missing = new();

In Tick():

_missing.Clear();

for (int i = 0; i < leaves.Count; i++)
{
    CdlodChunk c = leaves[i];
    long key = ChunkKey(c);

    if (_active.TryGetValue(key, out ChunkSlot slot))
    {
        slot.SeenFrame = _frame;
        ApplyChunk(slot, c, key, snapped);
    }
    else
    {
        _missing.Add(c);
    }
}

_missing.Sort((a, b) =>
{
    float da = ChunkPriorityDistanceSq(a, camPos, velXZ);
    float db = ChunkPriorityDistanceSq(b, camPos, velXZ);
    return da.CompareTo(db);
});

int births = 0;
for (int i = 0; i < _missing.Count && births < effOps; i++)
{
    BirthChunk(_missing[i], snapped: true);
    births++;
}

Helper:

private static float ChunkPriorityDistanceSq(CdlodChunk c, Vector3 camPos, Vector3 velXZ)
{
    Vector2 center = c.OriginXZ + new Vector2(c.Size * 0.5f, c.Size * 0.5f);
    Vector2 cam = new(camPos.X, camPos.Z);
    Vector2 to = center - cam;

    float d2 = to.LengthSquared();

    // Slightly favor chunks in direction of motion.
    Vector2 v = new(velXZ.X, velXZ.Z);
    if (v.LengthSquared() > 1f)
    {
        Vector2 vn = v.Normalized();
        float ahead = Mathf.Max(0f, to.Normalized().Dot(vn));
        d2 *= Mathf.Lerp(1.0f, 0.65f, ahead);
    }

    // Slightly favor smaller chunks because they are closer/visible detail.
    d2 *= Mathf.Clamp(c.Size / 8192f, 0.25f, 1.0f);

    return d2;
}

Then move the new-chunk creation block into:

private void BirthChunk(CdlodChunk c, bool snapped)

Why:
This makes the birth budget deterministic and useful. If the budget is limited, near/visible chunks should appear first.

7. Do not increase RetireGrace as a pop fix

Problem:
Your note already says increasing RetireGrace to 10 caused a new pop: parent chunks lingered over child replacements, causing overlap/shimmer/despawn artifacts. It says RetireGrace should stay low, because it bridges deferred births, not thrash.

Files:

scripts/lab/CdlodTerrain.cs

Keep:

public int RetireGrace = 2;

Why:
High retire grace hides holes but creates LOD overlap artifacts.

8. Make SetEnabled(true) force a full reapply

Problem:
SetEnabled(false) hides active/free instances. SetEnabled(true) only flips _enabled; it does not force existing active instances visible/repositioned/re-AABB’d. In your current code, re-showing depends on future ApplyChunk() conditions.

Files:

scripts/lab/CdlodTerrain.cs

Change:

public void SetEnabled(bool on)
{
    _enabled = on;

    if (on)
    {
        _forceReapply = true;
        _lastRenderOrigin = new Vector3(float.NaN, 0f, float.NaN);
    }
    else
    {
        foreach (var kv in _active) { kv.Value.Mi.Visible = false; }
        foreach (var mi in _free) { mi.Visible = false; }
    }

    GD.Print($"CdlodTerrain: {(on ? "ENABLED" : "disabled")}");
}

Why:
This prevents stale hidden instances when toggling CDLOD modes.

9. Dispose the field cache

Problem:
CdlodTerrain._ExitTree() disposes _aabbProvider, but not _fieldCache. The cache owns a pipeline/shader/texture-array RID and has its own Dispose() method.

Files:

scripts/lab/CdlodTerrain.cs

Change:

public override void _ExitTree()
{
    _aabbProvider?.Dispose();
    _fieldCache?.Dispose();
}

Why:
Not the immediate pop cause, but it is a correctness/resource leak fix.

Priority 2 — predictive loading without tap-thrash
10. Restore predictive loading, but speed-gate it

Problem:
Your note says setting PredictLookahead from 3 to 0 fixed tap-thrash, but made sustained fast-flight rebirths worse: 7.7% vs under 1%.

So both extremes are bad:

PredictLookahead = 3  -> tap-thrash
PredictLookahead = 0  -> fast-flight rebirth churn

Files:

scripts/lab/CdlodTerrain.cs

Fix:
Keep public value, but apply it only above a speed threshold.

Add fields:

public float PredictMinSpeed = 800f;
public float PredictFullSpeed = 2500f;
public float PredictMaxLookahead = 2.0f;

Replace:

Vector3 bias = velXZ * Mathf.Max(0f, PredictLookahead);

with:

float speed = velXZ.Length();
float predictT = Mathf.InverseLerp(PredictMinSpeed, PredictFullSpeed, speed);
predictT = predictT * predictT * (3f - 2f * predictT); // smoothstep

float lookahead = Mathf.Min(PredictLookahead, PredictMaxLookahead) * predictT;
Vector3 bias = velXZ * Mathf.Max(0f, lookahead);

Default values:

public float PredictLookahead = 1.5f;
public float PredictMinSpeed = 800f;
public float PredictFullSpeed = 2500f;

Why:
Tiny taps get no prediction. Sustained high-speed flight gets enough forward bias to prevent rebirth churn.

11. Add hysteresis to velocity itself

Problem:
You already smooth velocity in TerrainLabUI.Process.cs, but a tap still caused predictive center movement before. If you restore prediction, make the prediction use “confirmed movement,” not one-frame spikes.

Files:

scripts/lab/TerrainLabUI.Process.cs
scripts/lab/CdlodTerrain.cs

Fix idea:
In TerrainLabUI.Process.cs, add a slower velocity for streaming:

private Vector3 _streamVelSmoothed;

Then:

_streamVelSmoothed = _streamVelSmoothed.Lerp(inst, 0.04f);
_terrain.CdlodTick(camPos, _streamVelSmoothed);

Keep _camVelSmoothed for the HUD.

Why:
The HUD can respond quickly. Streaming should respond slowly enough not to thrash.

Priority 3 — AABB / culling / shadow stability
12. Throttle AABB tightening harder

Problem:
ChunkAabbProvider.HeightRange() uses BufferGetData() on the render thread. It does not block the game thread, but it still syncs GPU work on the render thread. The provider default is 8 requests/frame.

Files:

scripts/lab/ChunkAabbProvider.cs
scripts/lab/CdlodTerrain.cs

Change default:

public int MaxRequestsPerFrame = 2;

or even:

public int MaxRequestsPerFrame = 1;

Why:
This reduces render-thread hitching while streaming.

13. Only tighten AABBs for shadow-relevant / near chunks

Problem:
Far chunks outside the useful shadow range do not need precise AABBs. Tightening every birth burns render-thread work and can cause culling bugs if a probe misses extremes.

Files:

scripts/lab/CdlodTerrain.cs

Change:

private bool ShouldTightenAabb(CdlodChunk c, Vector3 camPos)
{
    float half = c.Size * 0.5f;
    Vector2 center = c.OriginXZ + new Vector2(half, half);
    Vector2 cam = new(camPos.X, camPos.Z);
    float dist = (center - cam).Length();

    // Tune. Start conservative.
    return dist < 12000f || c.Size <= 1024f;
}

Then replace:

if (TightenAabb) { _aabbProvider.Request(key, c.OriginXZ, c.Size); }

with:

if (TightenAabb && ShouldTightenAabb(c, camPos))
{
    _aabbProvider.Request(key, c.OriginXZ, c.Size);
}

Why:
Near chunks need tight bounds for shadows/culling. Far chunks can keep generous bounds.

14. Add an AABB safety mode for debugging

Problem:
If chunks disappear, you need to know whether it is logical streaming or render culling.

Files:

scripts/lab/CdlodTerrain.cs
scripts/lab/TerrainLab.cs
scripts/lab/TerrainLabUI.Cli.cs

Add CLI/debug mode:

--generousaabb

That forces:

TightenAabb = false;

and maybe uses an even larger fallback:

return (_minH - 1000f, _maxH + 1000f);

Why:
If disappearing stops with generous AABB, it is culling/AABB. If it continues, it is streaming/cache.

Priority 4 — shadows
15. Treat shadows as two separate systems: near CSM and far terrain self-shadow

Problem:
Your uploaded note says the blocky/popping far shadows are a pre-existing 6 km CSM cutoff made more obvious by the 64 km view distance.

Files:

scripts/lab/LightingComposer.cs
scripts/lab/LightingState.cs
shaders/ground.gdshader
data/lab_controls.json

Rule:

Godot CSM = near/mid contact shadows
Custom horizon shadow = far terrain form shadow

Do not try to make CSM cover 64 km. It will destroy near shadow quality.

16. Keep ShadowMaxDist around 4000–6000, not huge

Problem:
ShadowMaxDist default is 6000. Your code comment says testing 500 m vs 6 km vs 24 km was visually identical for the low-sun vista and that extending CSM is not the lever for long-range terrain shadows.

Files:

scripts/lab/LightingState.cs
scripts/lab/LightingComposer.cs
data/lab_controls.json

Recommended default:

public float ShadowMaxDist = 6000f;

Maybe test:

--shadowdist=3000
--shadowdist=6000

Why:
Near shadow texel density matters more than fake far coverage.

17. Default horizon shadows off until streaming is stable

Problem:
ground.gdshader has hz_on = true. This is a per-pixel macro-height march toward the sun. It can be visually useful, but it can also make terrain look like it has weird shadow blocks/dark bands while you are debugging streaming.

Files:

shaders/ground.gdshader
data/lab_controls.json

Change for stabilization:

uniform bool hz_on = false;

Then use the P key to A/B it live.

Why:
Do not debug chunk popping while a custom far-shadow march is adding large-scale darkness changes.

18. Keep SSIL off

Problem:
Your code already disables SSIL and the comments say SSIL was causing view-angle-dependent dark mud / “anti-sun darkness.”

Files:

scripts/lab/LightingComposer.cs

Keep:

env.SsilEnabled = false;

Why:
This is likely not your current chunk issue, but turning it back on will confuse the shadow review.

19. Keep SSAO subtle

Problem:
The code already lowered SSAO to 0.6 because it was acting like a harsh second shadow system.

Files:

scripts/lab/LightingComposer.cs
data/lab_controls.json

Keep or lower:

env.SsaoIntensity = 0.4f; // or 0.6f

Why:
Heavy SSAO on terrain often looks like fake shadow noise.

Priority 5 — view distance / fog / far clip
20. Decide whether the new far-view look stays

Problem:
The note says the depth fog + far-clip changed the look and this is a taste/eye-gate choice, not a pure bug. It also says the old fog hid the seam with haze and capped clear view around 16 km, while the new mode uses zero fog until 22 km and ramps to 62 km.

Files:

scripts/lab/LightingComposer.cs
scenes/terrain_lab.tscn
scenes/review.tscn
scripts/lab/TerrainLabUI.Process.cs
scripts/lab/AtmosphereCompute.cs
scripts/lab/AerialPerspective.cs

Recommended temporary stabilization:

FogDepthBegin = 12000f;
FogDepthEnd = 36000f;
FogDepthCurve = 2.5f;

and:

LoadRing = 4
FarClip = 32000 or 36000

Why:
64 km view distance is not the first stable target. Get 32–36 km stable, then increase.

21. Keep far clip aligned with loaded radius / aerial range

Problem:
The note says if the far clip changes, all aerial/atmosphere range values must match, or distant terrain loses haze/mis-colors past the old range.

Files:

scenes/terrain_lab.tscn
scripts/lab/AerialPerspective.cs
scripts/lab/TerrainLabUI.Process.cs
scripts/lab/AtmosphereCompute.cs

Rule:

Camera FarClip
AerialPerspective far
AtmosphereCompute aerial far
TerrainLabUI.Process SetCamera far argument

must be changed together.

Priority 6 — startup / baseline cost
22. Stop building the full 2048² single mesh when CDLOD is the default

Problem:
TerrainLab.Build() generates a full height page and builds a full PlaneMesh using HeightmapRes - 1 subdivisions before CDLOD is enabled. That means CDLOD mode still pays a heavy single-mesh startup/memory cost. The code builds the height texture/mesh first, then creates CDLOD as a sibling.

Files:

scripts/lab/TerrainLab.cs
scripts/lab/TerrainLabUI.cs
scripts/lab/TerrainLabUI.Cli.cs

Fix direction:

BuildSingleMesh(...)
BuildCdlodOnly(...)
BuildSharedTerrainMaterial(...)

Default runtime should call:

BuildCdlodOnly(_fc, _params);

Review/single-mesh mode can call:

BuildSingleMesh(_fc, _params);

Why:
You should not build a huge hidden mesh just to use CDLOD.

23. Move CLI/default mode decision earlier

Problem:
Right now _terrain.Build(_fc, _params) happens before CLI overrides are applied, so the build path cannot know whether CDLOD is actually needed.

Files:

scripts/lab/TerrainLabUI.cs
scripts/lab/TerrainLabUI.Cli.cs

Fix direction:

ParseCliEarlyTerrainModeOnly();
_terrain.Build(..., buildMode);
ParseCliRest();

Why:
Build-time decisions need to happen before expensive build-time work.

Priority 7 — instrumentation / proof
24. Improve stream diagnostics

Problem:
You already have --streamdbg, and the log is good: leaves, active, births, capped, bake pending, cache pending, tights, snaps. The note says this should be used to see whether far-ring births are deferred vs near LOD splits eating the budget.

Files:

scripts/lab/CdlodTerrain.cs

Add to log:

missing count
missing near count
missing far count
free cache layers
pending delayed free layers
rebirths/30

Example:

GD.Print($"[streamdiag] f={_frame} leaves={leaves.Count} active={_active.Count} missing={_missing.Count} births/30={_dbgBirthsAcc} capped={_dbgCapped}/30 bakePend={bakePend} cachePend={_cachePending.Count} freeLayers={_freeLayers.Count} delayedFree={_cacheLayersPendingFree.Count} rebirths={TotalRebirths}");

Why:
You need to know whether the visible issue is missing births, cache backlog, or cache-layer starvation.

25. Add a visible “uncached chunk” debug tint

Problem:
If chunks are born but not cache-ready, they fall back to live analytic field eval. That should look the same, but costs more. If the fallback path has an issue, you need to see it.

Files:

shaders/ground.gdshader
scripts/lab/CdlodTerrain.cs

Add debug uniform:

uniform bool debug_cache_ready = false;

In fragment:

if (debug_cache_ready && use_chunk > 0.5) {
    ALBEDO = mix(ALBEDO, cache_ready > 0.5 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), 0.5);
}

Why:
If brown rectangles only appear in red/uncached chunks, the issue is fallback/live path. If they appear after green/cache-ready, the issue is cache-layer content.

26. Add a “chunk cache slot” debug color

Problem:
Cache slot reuse bugs are hard to see.

Files:

shaders/ground.gdshader
scripts/lab/CdlodTerrain.cs

Debug output:

if (debug_cache_slot && use_chunk > 0.5) {
    float s = fract(chunk_slot * 0.03125);
    ALBEDO = vec3(fract(s * 3.1), fract(s * 7.3), fract(s * 13.7));
}

Why:
If a chunk changes color without changing chunk identity, your cache slot assignment/reuse is unstable.

Priority 8 — actual code organization fixes
27. Split Tick() into reconcile phases

Problem:
CdlodTerrain.Tick() does too much: render-origin snap, selection, birth, retire, AABB drain, cache drain, cache pump, stream diagnostics. That makes it easy to create order bugs.

Files:

scripts/lab/CdlodTerrain.cs

Split into:

UpdateRenderOrigin(camPos);
SelectLeaves(camPos, velXZ);
DrainAsyncResults();
MarkExistingAndCollectMissing();
BirthMissingChunks();
RetireDeadChunks();
PumpAsyncWork();
PrintDiagnostics();

Why:
This will make future bugs much easier to isolate.

28. Make chunk birth a named method

Problem:
Birth logic is embedded in the main for loop. It needs to be reused after sorting missing chunks.

Files:

scripts/lab/CdlodTerrain.cs

Add:

private void BirthChunk(CdlodChunk c, long key)
{
    if (_recentRetire.TryGetValue(key, out int rf) && _frame - rf < 30)
    {
        TotalRebirths++;
    }

    ChunkSlot ns = AcquireSlot();
    ns.SeenFrame = _frame;
    _active[key] = ns;

    if (FieldCache && _fieldCache != null && _freeLayers.Count > 0)
    {
        ns.CacheSlot = _freeLayers.Pop();
        ns.CacheReady = false;
        _fieldCache.Request(key, ns.CacheSlot, c.OriginXZ, c.Size);
    }

    if (TightenAabb && ShouldTightenAabb(c, _lastCamPos))
    {
        _aabbProvider.Request(key, c.OriginXZ, c.Size);
    }

    ApplyChunk(ns, c, key, snapped: true);
}

Why:
This lets the selection/reconcile logic stay readable.

Priority 9 — test matrix

Run tests in this order after each major change.

A. Stable baseline
--cdlod=1 --loadring=4 --chunkops=24 --fieldcache=1 --bakereq=16 --streamdbg --profile=8 --profmove

Expected:

active roughly tracks leaves
capped not 30/30 constantly
bakePend does not grow forever
rebirths low
no brown rectangle
B. Cache off
--cdlod=1 --loadring=4 --fieldcache=0 --streamdbg --profile=8 --profmove

If brown rectangles disappear only with cache off, the cache path is the culprit.

C. AABB off
--cdlod=1 --loadring=4 --notighten --streamdbg --profile=8 --profmove

If disappearing chunks stop, the AABB/culling path is the culprit.

D. Shadows off
--cdlod=1 --loadring=4 --shadow=0 --ssao=0 --ssil=0 --streamdbg

Then re-enable one at a time.

E. LOD visualization
--cdlod=1 --loadring=4 --lodviz=1

If the pop lines up with LOD bands, it is LOD handoff. If it does not, it is cache/AABB/render-origin.

Final recommended fix order

Do this exact order:

Set LoadRing = 4.
Remove velocity-scaled birth budget from default path.
Delay cache-layer reuse.
Force reapply in SetEnabled(true).
Dispose _fieldCache.
Sort missing chunk births by priority.
Keep RetireGrace = 2.
Throttle AABB requests to 1–2/frame.
Only tighten AABB near/shadow-relevant chunks.
Restore predictive loading only with speed-gating.
Default horizon shadows off until streaming is clean.
Keep CSM range around 4–6 km; do not stretch it to the far clip.
Align far clip / aerial range / fog range if you keep the long-view mode.
Later: split CDLOD-only build from single-mesh build.

The most important mental model is this:

Budget issue      -> missing/delayed chunks
Cache-slot issue  -> wrong/flat/shifting chunks
AABB issue        -> chunk exists but renderer culls it
LOD handoff issue -> parent/child pop or overlap shimmer
Shadow issue      -> blocky/popping darkness, not actual geometry loss

So I would not chase shadows first. I would stabilize streaming + cache + AABB first, then tune shadows once the geometry is trustworthy.
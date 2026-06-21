# AUDIT BRIEF — Is the S1 analytic-field 34 ms real and irreducible?

**For a fresh chat with zero context. Read this top-to-bottom; it is self-contained.**
Your ONE job: independently judge whether the measured **34 ms/frame** cost of the live (analytic)
heightfield render is **genuine and irreducible**, or whether there is **low-hanging fruit** that cuts
it cheaply — BEFORE the next arc (S2, a quadtree LOD) is designed around that number. This is a
**read-and-analyze audit**, not a build task. Propose findings; do not implement.

## Why this matters (the stakes)
WG16 (Godot 4.6.2 mono, C# + GPU compute) is building infinite procedural terrain. Stage S1 just made
the ground shader displace from the heightfield function **live in the vertex shader** (instead of
sampling a pre-baked texture). It measured **34.0 ms avg in motion** vs **5.9 ms** for the baked path —
**4.3× over the project's 8 ms budget**. The next stage (S2) is a quadtree that cuts vertex count to
bring the live field under budget. **If 34 ms is inflated by a fixable inefficiency, S2's whole premise
shifts** — so we want an independent second pair of eyes on the number before committing to it.

## The pillars (the standard for any recommendation)
> quality = performance = AAA-ish = long-term-best, regardless of time cost; lead with the
> most-correct option; never a cheap shortcut as the default.
So: prefer findings that make the field genuinely cheaper WITHOUT changing the look or regressing
correctness. A fix that reintroduces popping or visual change is NOT a win.

## Exactly what to audit (the files)
1. **`shaders/field_math.gdshaderinc`** — the shared heightfield math (ONE source of truth; the compute
   bake AND the spatial ground shader both use it). This is the per-vertex hot code. Look here first.
   - `field_height(world_xz, seed, spacing, FieldP fp)` composes: `continent()` (5 octaves of value_fbm
     + a domain_warp), `uplift()` (multiple value_fbm calls: center/width/massif/rough + a domain_warp),
     `slope_damped_fbm()` (6 octaves, analytic derivatives), `oriented_ridges()`→`ridged_fbm()` (6
     octaves + a domain_warp). Several **domain_warp** calls, each = 2 more value_noise evals.
2. **`shaders/ground.gdshader`** — the spatial consumer. KEY per-vertex structure in `vertex()` under
   `if (use_analytic)`: it calls `analytic_h(wxz)` for the height **PLUS 4 more** `analytic_h()` calls
   at ±spacing offsets for the normal → **5 full `field_height` evaluations per vertex**. That 5× is a
   prime suspect (could a cheaper normal — e.g. analytic derivatives the field already computes in
   `value_noise_d`, or a single-tap screen-space normal — replace 4 of the 5 evals?).
3. **`scripts/lab/TerrainLab.cs`** — the presenter. TWO things to weigh:
   - The render mesh is a **2048² PlaneMesh = ~4.19M vertices, NO LOD** (`SubdivideWidth/Depth =
     HeightmapRes-1`). The 34 ms is this whole mesh evaluated every frame. (S2 is meant to fix the
     vertex COUNT; this audit is about the per-vertex COST and any redundant whole-mesh passes.)
   - **Shadow casting:** the main mesh `CastShadow = On` and the scene uses a sun with PSSM cascades →
     the analytic vertex shader likely runs **again per shadow cascade** (up to ~4×) on top of the
     camera pass. **Is the heavy analytic field running in the shadow pass too?** If so, the shadow
     cascades could displace from the CHEAP baked texture (or the coarse `_giProxy`) with no visible
     difference — a potentially large, safe win. Verify whether this is happening and whether it's the
     bulk of the 34 ms.
   - A second mesh `_giProxy` shares the SAME material (`MaterialOverride = _mat`); default OFF
     (`GIMode.Disabled`, `CastShadow.Off`). Confirm it isn't silently adding a second analytic pass.
4. **`data/field_params.json`** — live values: `octaves=6`, `cont_octaves=5`, `heightmap_res=2048`,
   `region_size_m=8192`. (Are all 6 octaves contributing, or are high octaves band-limited to ~0 at this
   spacing and effectively dead weight per vertex? `octave_weight()` in field_math gates octaves by
   wavelength-vs-spacing — check whether several octaves evaluate to ~0 and could be skipped.)

## The measurement (so you can reproduce / trust it)
- Tool: `--profile=5 --profmove` (orbits the camera 5 s in motion, prints `PROFILE: avg … ms`, quits).
- Result: baked `PROFILE: avg 169 fps (5.9 ms) worst 7.2 ms`; analytic `avg 29 fps (34.0 ms) worst
  39.3 ms`. GPU: RTX 5090 Laptop. So the 34 ms is GPU-bound vertex work, in motion, averaged over 148
  frames — a solid number, not a one-frame fluke.
- **⚠ RUN-INVOCATION RULE (or commands silently no-op):** user `--flags` MUST come after a bare `--`
  separator (`OS.GetCmdlineUserArgs()` is empty without it → flags ignored, scene never quits). And
  `--auto-shot=` needs a real OS path (e.g. `C:/tmp/wg16shots/x.png`), NOT `user://` (else IOException
  every frame). Canonical:
  `…Godot…_console.exe --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=1 --profile=5 --profmove`
- ONE Godot at a time (two contend for the GPU → grey hang). Kill strays:
  `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. The scene CANNOT run `--headless`
  (`FieldCompute` local RenderingDevice NullRefs). `--analytic=1` = live field, `--analytic=0` = baked.

## The specific questions to answer
1. **Is the 34 ms genuinely the field math**, or is a chunk of it the **5× per-vertex evaluation** (1
   height + 4 normal taps)? Could a cheaper normal cut that to ~1–2× with no visible change?
2. **Is the heavy analytic field running in the PSSM shadow pass** (×~4)? If yes, can the shadow/GI
   passes use the baked texture or the coarse proxy instead — a safe, possibly large win?
3. **Are all 6/5 octaves doing work** at this spacing, or are high octaves band-limited to ~0 and
   skippable per vertex (early-out when `octave_weight` ≈ 0)?
4. **Any redundant recomputation** inside `field_height` — sub-terms (e.g. the domain_warps, the
   `uplift()` internals) computed more than needed, or shareable across the 5 taps?
5. **Bottom line:** after the cheap wins (if any), what is the realistic per-vertex floor — i.e. is the
   premise "the quadtree's vertex reduction will bring this under 8 ms" sound, or is the field so heavy
   per-vertex that a different height-source (hybrid: analytic morph + sampled value) is the
   pillar-correct call? (Design context: the spec already names hybrid + field-cost-reduction as the two
   fallbacks if pure-analytic stays over budget.)

## Where the full design context lives (read if you need it)
- `docs/superpowers/specs/2026-06-21-infinite-terrain-cdlod-design.md` — the arc design; **§10 "S1
  result"** has this exact perf finding + the 3 paths (proceed to S2 / hybrid / reduce field cost).
- `docs/superpowers/plans/2026-06-21-s1-analytic-field-parity.md` — what S1 built (4 tasks), incl. why
  the field had to be relocated to a shared include (Godot spatial `#include` works; RD-GLSL has no
  `#include`, so C# string-splices the same file into the compute source).
- Memory/guardrail: the project's terrain history died 15× at LOD pops; **CDLOD not clipmap**; the
  user's live eye in motion is the only LOOK gate (never a downscaled still).

## Deliverable
A ranked list of findings: for each, **what** the cost is, **how much** it plausibly saves, whether it
**changes the look/correctness** (must not), and **how to verify**. End with the bottom-line call on
question 5. Do NOT implement — this is the second pair of eyes before S2.

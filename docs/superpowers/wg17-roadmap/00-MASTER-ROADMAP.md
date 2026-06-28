# WG17 Terrain Engine — Master Roadmap

**Last updated:** 2026-06-28
**Repo:** `C:\Wg16\WG17\terrainengine-10k` (fresh Godot 4.6 / C# / Forward+ / D3D12)
**Reference (source to port/learn from):** `C:\Wg16\wg-16-project` (WG16) + its
[migration audit](../../MIGRATION-AUDIT-2026-06-28.md).

> **What WG17 is:** a from-scratch, modular re-creation of the WG16 terrain engine — a **terrain engine that
> games consume to build procedural worlds**, shipped as a self-contained Godot addon. Built one slice at a
> time as focused **port-clean rewrites** (proven architecture, cleaner code, fix-on-port the known bugs),
> each ending at a quick eye-gate + a profile number. Quality = performance = AAA-ish (the WG16 pillar).

---

## Status at a glance

| # | Slice | Spec | Plan(s) | State | Result |
|---|---|---|---|---|---|
| 1 | **Base geometry** (Field + CDLOD) | [spec](01-terrain-base-geometry.md) | ✅ | 🟢 **SHIPPED** | flying 4.2ms (WG16 7.2), 6 checks PASS, shadowless by design |
| A | **Lighting** (composer + ShadowRegistry) | [spec](02-lighting.md) | ✅ | 🟢 **SHIPPED** | day/night cohesive, view-locked, 0 shadow owners, 4.18ms |
| B | **Atmosphere** (Hillaire — PORT, validated) | [spec](03-atmosphere.md) | ✅ | 🟢 **SHIPPED** | physical sky + aerial, --atmoscheck PASS, view-locked, 4.18ms (free at steady state) |
| C | **Clouds** (port + 3 fixes) | [spec](04-clouds.md) | ✅ | 🟡 staged | — |
| D | **Godrays** (port) | [spec](05-godrays.md) | ✅ | 🟡 staged | — |
| S | **Terrain surfacing** (PBR materials) | [spec](06-surfacing.md) | ✅ | 🟡 staged | — |
| X | **Control surface** (config + UI + addon) | [spec](07-control-surface.md) | ✅ | 🟡 staged | — |

🟢 built & gated · 🟡 specced + planned + kickoff ready, not executed.

**Next unplanned modules:** Water/Erosion (reuses the `IHeightSource` seam), Material Board / Workbench,
then world-cohesion features (biomes/trees/caves/roads/POIs — see the AAA dossier).

---

## The architectural spine (carried across every slice)

These principles were established early and every slice obeys them — they're *why* WG17 won't repeat WG16's
failures:

1. **GPU quarantine.** `RenderingDevice`/`RenderingServer`/`CallOnRenderThread` live ONLY in the compute
   classes (FieldCompute, ChunkFieldCache, AtmosphereCompute, CloudVolume, …). Logic/scene code never touches RD.
2. **One-way seams, no host callbacks.** Subsystems talk through narrow interfaces in one direction
   (`IHeightSource`, `ILuminaryFeed`, `IAtmosphereFeed`, `ILightingTarget`). No bidirectional god-host
   (WG16's `TerrainLabUI`/`ILightingHost` disease).
3. **Single source of truth.** Lighting has one writer (`LightingComposer`). The control surface has one config
   (`TerrainConfig`). No subsystem keeps a duplicate enable/param → no state drift.
4. **Texture2Drd assign-once.** Compute→material textures bind their RID ONCE; contents update via compute,
   RID never reassigned per frame — guards open Godot bug #118292 (CompositorEffect render-thread RID race).
5. **View-locked lighting.** Lighting/atmosphere/cloud *lighting* derive from sun+world, never camera. Godrays
   are the one legitimately view-dependent layer (and say so). "Weird when I mouse-look" = surfacing, not shadows.
6. **Shadows: one owner per band, enforced.** `ShadowRegistry` asserts ≤1 ground-shadow owner; 0 until a
   deliberate shadow slice. No hidden re-enable paths (WG16's recurring reset bug).
7. **Fix-on-port, don't port bugs.** Each slice fixes its flagged WG16 bugs as it ports (lighting: no EMISSION
   fill / no hardcoded ambient / no mood-dict shim; clouds: double-alpha / sun-extinction / dead octave;
   atmosphere AT-2: the blue-band rewrite; surfacing: no view-relief-fade / no renderer darkening).
8. **Per-slice discipline.** Quick eye-gate (look in motion) + a profile number + the slice's numeric checks,
   then move on. Don't tune past "looks right + isn't slower"; deep optimization is its own task.

---

## Build order & dependencies

```
   Field+CDLOD ──► (everything renders on it)
        │
        ├──► Lighting (A) ──► ILuminaryFeed ──┬──► Atmosphere (B) ──► IAtmosphereFeed ──► Clouds (C) ──► Godrays (D)
        │                                     └──► Clouds (C) [sun/moon]
        ├──► Surfacing (S)  [reads the field; benefits from lighting]
        └──► Control surface (X)  [config drives ALL of the above; each module adds a config block + panel]

   Water/Erosion ──► reuses IHeightSource (same primitive the field cache uses)
```

- **Built:** Field+CDLOD, Lighting, **Atmosphere (B)**. **Order for the rest:** C → D (sky stack, each reads the prior);
  Surfacing any time; Control surface integrates whatever exists (and supersedes "minimal driver per module").
- **Graceful degradation:** each downstream slice works (uglier) if an upstream one isn't merged — so order is
  flexible, not rigid.

---

## How to execute a slice

Each slice has a **kickoff prompt** — paste it into a fresh Claude Code chat; it reads the spec+plan(s) and
runs task-by-task via subagent-driven-development, pausing at your eye-gate. Kickoffs:

| Slice | Kickoff |
|---|---|
| Terrain | `../plans/2026-06-28-wg17-KICKOFF-PROMPT.md` |
| Lighting (A) | `../plans/2026-06-28-wg17-sliceA-KICKOFF-PROMPT.md` |
| Atmosphere/Clouds/Godrays (B/C/D) | `../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md` |
| Surfacing | `../plans/2026-06-28-wg17-surfacing-KICKOFF-PROMPT.md` |
| Control surface | `../plans/2026-06-28-wg17-control-surface-KICKOFF-PROMPT.md` |

**Environment:** Godot 4.6 mono at
`C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`.
`dotnet build Terrainengine10k.csproj` after every `.cs` edit; absolute `--path`; bare `--` before user flags;
compute runs windowed (local RD), not `--headless`.

---

## Per-slice index

- [01 — Base geometry (Field + CDLOD)](01-terrain-base-geometry.md) 🟢
- [02 — Lighting](02-lighting.md) 🟢
- [03 — Atmosphere (port, validated)](03-atmosphere.md) 🟢
- [04 — Clouds](04-clouds.md) 🟡
- [05 — Godrays](05-godrays.md) 🟡
- [06 — Terrain surfacing](06-surfacing.md) 🟡
- [07 — Control surface (config + UI + addon)](07-control-surface.md) 🟡
- [08 — Backlog & future](08-backlog.md)

Each per-slice page links its spec + plan(s) + kickoff and records the outcome once shipped.

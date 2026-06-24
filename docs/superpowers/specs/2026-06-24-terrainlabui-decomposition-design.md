# TerrainLabUI God-Class Decomposition — Design

**Date:** 2026-06-24
**Status:** approved (scope: Phases 1–3)
**Goal:** Reduce the ~3,200-LOC `TerrainLabUI` partial (15 files) to a focused coordinator (~1,400 LOC)
by extracting cohesive subsystems into real classes, behavior-preserving, with no look/behavior change.

## Background

`TerrainLabUI` (namespace `WG16.Lab`, `scenes/terrain_lab.tscn`) is the look-lab UI god-class flagged as the
"central maintainability risk" in `docs/AUDIT-2026-06-24.md` (#2). The consolidation pass already extracted
`LabShots` (proving the pattern, alongside the prior `LightingComposer`). This design covers the larger,
registry-coupled extractions, mapped by three code-explorer passes.

## The shape of the problem

It is **one spine with satellites**, not 15 equal pieces.

- **The spine = the control registry:** `_byId` (Dictionary), `_controls` (List), the `LabControl` inner type
  (which fuses JSON *schema* with live Godot *widget refs*), the `_ready` re-entry gate, and
  `SetWidgetValue`/`SetWidgetValueSilent`. Every value flows registry → `ApplyControl(LabControl, bool)` (one
  type-switch in Apply.cs) → `ComposeLighting()` (already a forwarder into the extracted `LightingComposer`).
- **The linchpin:** most satellite extractions are blocked on giving the registry a **narrow interface**
  (`ILabControls`). Once it exists, four satellites fall out cleanly; before it, they don't.

## Target architecture

New standalone classes (each in its own file under `scripts/lab/`), depending on `TerrainLabUI` only through
narrow surfaces (delegates / interfaces), mirroring how `LightingComposer` and `LabShots` already work:

| Class | From | Surface it needs |
|---|---|---|
| `CliArgs` (struct) + `CliArgs.Parse()` | Cli.cs `ParseCli` | none (pure parse) |
| `LuminaryCheckRunner` | LumCheck.cs | `ComposeLighting`, `ApplyLuminaryDicts` (2 delegates) |
| `LabCaptureSequences` | Process.cs auto-shot/godrayab/fillab | viewport, `GetTree().Quit`, `_godraysScreen`, `FillEnabled`+`ComposeLighting` |
| `LabPerfProbe` | Process.cs profile/profmove/aabbspike | camera, `_fc`, `_params`, `_spikeProvider` |
| `ILabControls` (interface) + top-level `LabControl` | TerrainLabUI.cs / Registry.cs | — (the linchpin) |
| `SkyPresets` | Sun/Celestial/Fantasy/Moods presets | `ILabControls`, `_cloud`, moon/skyTint state, `ComposeLighting` |
| `PresetsManager` | UserPresets.cs | `ILabControls`, `_luminaryList`, disk path |
| `LabRandomizer` | Randomize.cs | `ILabControls`, `_rng`, `RandomizeCloud` delegate |
| `LabReviewController` | Review.cs | `ILabControls` `Set()` shim, terrain/camera/sun, presets, mood, time |

### `ILabControls` (the linchpin interface)

```
bool IsReady { get; set; }                    // the _ready re-entry gate
IReadOnlyList<LabControl> Controls { get; }   // iteration (randomize/presets/baseline)
bool TryGet(string id, out LabControl c);     // _byId.TryGetValue
void SetValue(LabControl c, Variant v);       // = SetWidgetValue (suppresses _ready, applies)
void SetValueSilent(LabControl c, float v);   // = SetWidgetValueSilent (display only)
```

`LabControl` lifts to a **top-level type** (currently a private inner class). Its widget refs (`Widget`,
`ValLabel`, `LockBox`) stay on it for now — splitting schema vs widget is **out of scope** (YAGNI; it adds
a DTO layer with no current consumer). `TerrainLabUI` implements `ILabControls` (thin, over its existing
fields), so satellites take an `ILabControls` rather than the whole god-class.

## What stays in TerrainLabUI (explicitly NOT extracted — Phase 4, deferred)

These are frame-loop-bound or are the application context itself; extracting them moves coupling without
reducing it, and risks regressing streaming/camera/look:

- **`_Process` core:** floating-origin CDLOD frame (writes the camera, drives terrain/water/cloud render
  origin), atmosphere camera push, the ~90-line debug-key bank (B/N/M/…/J toggles spanning every subsystem),
  the AT-1/2/3 + cloud-shadow one-shot RID-activation gates, day/night cycle advance.
- **`ApplyControl` dispatch + `ApplyCloud*`** (the apply spine).
- **`ApplyCliOverrides` / `AttachClouds`** (the deferred CLI application context — ~22–28 member surface each).
- **`LoadRegistry` / `BuildPanel` / `BuildRow`** (the registry loader + panel builder; the panel builder
  could later become `LabPanelBuilder` but is out of scope here).

End state: `TerrainLabUI` = lifecycle (`_Ready`), the registry (+ `ILabControls` impl), the apply dispatch,
the frame loop, and the panel build — a coordinator, ~1,400 LOC.

## Sequencing & risk

- **Phase 1 (LOW, no interface):** `CliArgs` (+ delete 4 dead flags `_terrainArCli`/`_terrainDetailCli`/
  `_groundRulesCli`/`_greviewCli`), `LuminaryCheckRunner`, `LabCaptureSequences` + `LabPerfProbe`.
  ~250 LOC out, near-zero risk, each independently committable.
- **Phase 2 (LOW, enabling):** top-level `LabControl` + `ILabControls` + `TerrainLabUI` implements it.
  No behavior change. Unlocks Phase 3.
- **Phase 3 (MED):** `SkyPresets` (merge Sun+Celestial+Fantasy+Moods — co-location removes Fantasy's
  cross-calls), `PresetsManager`, `LabRandomizer`, `LabReviewController`. ~1,000 LOC out.

## Verification (no unit tests exist — this is a Godot look-lab)

Each task ends GREEN on: `dotnet build` 0 errors; windowed boot via `--auto-shot=C:/tmp/x.png` (full UI builds,
no exceptions); relevant self-checks exit 0 (`--shadowcheck`, `--fieldcheck`, `--cdlodcheck` as touched);
and for any behavior-bearing change, a drift-free A/B (frozen time / single launch). "Behavior-preserving"
= byte-behavior-identical, proven per step. Commit per task on `experiment/presentation`.

## Non-goals

- No split of `LabControl` into schema/widget DTOs (YAGNI).
- No extraction of the frame-loop core, CLI appliers, or panel builder (Phase 4, deferred).
- No functional/look changes. Pure structure.

## Corrections to the audit found during the dive

- Audit #12 ("ObjectListCheck/LuminaryPresetCheck run unconditionally every startup") is **inaccurate** —
  they are CLI-gated and `Quit()` immediately; `LumCheck` is a no-op (`if (_lumCheckT < 0) return;`). Not a bug.
- 4 CLI flags are dead (`--ar`, `--detail`, `--groundrules`, `--greview` — stripped-ground leftovers); delete.

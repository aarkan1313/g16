# Sky lane — NEXT STEPS (2026-06-21)

Where the WG16 sky/light lane stands after today's session, and what's next. Read this first when picking the
lane back up. Authoritative logs: `docs/ROADMAP.md`, `docs/DECISIONS.md`, `docs/NEEDS_REVIEW.md`.

## Where we are

The sky lane is ~90% built and mostly gated. Done + gated: **Stage 3 night** (moon/moonlight/stars) ·
**#2 Clouds** (CO-1..CO-4) · **#3 GPU atmosphere** (AT-1 sky / AT-2 aerial / AT-3 cloud-light, all default-on) ·
**#4 Stage 4** (auto day/night cycle + fantasy presets).

**This session:**
- **C1 procedural galaxy/nebula → CLOSED as REJECTED.** Built the full baked galaxy+nebula+starfield system and
  iterated it live many rounds (localized the galaxy, killed a bloom glow, ridged nebulae, made a galaxy body
  read). User verdict: **"it doesn't work."** Night sky settled to **tuned moon + starfield** (stars −60% count /
  −70% slower blink — PASS). Galaxy `mw_brightness` 0, nebulae `neb_count` 0; machinery dormant + **zero
  per-frame cost** (early-out at brightness 0). Code NOT deleted — parked.
- **C2 meteors / shooting stars → PASS** ("pretty good"). Procedural in-shader (`cloud_sky.gdshader` `meteors()`),
  occasional ~1 s streaks with a glowing head + trail, per-meteor color variety, gated night×horizon, ~free when
  idle. Night-tab knobs: `meteors on` / `rate` / `brightness` / `length` / `speed` / `color` / `color variety`;
  `--meteordebug` forces one. Default-on. Commits 29c52e4 · 14ec581 · 371332d.

> **⚠ UPDATED 2026-06-21 (late) — this NEXT list below the line is SUPERSEDED.** After this doc was first written,
> several of its items resolved the same day. Current truth lives in `docs/ROADMAP.md` (sky lane #1-#7). Quick delta:
> - **Galaxy / Nebula → KILLED, not "start over."** The C1-v2 redo (billboards: structured generator → domain-warped
>   → volumetric raymarch → lit self-shadowed volumetric) was built and ALL read fake within the ~1 ms budget →
>   feature **removed entirely** (commit 62a3835). Do NOT re-attempt procedurally; only authored/offline textures or
>   >1 ms lit volumetrics could clear the bar, neither in scope. Night sky = **moon + stars + meteors**, settled.
> - **C2 planets + named stars + north star → PASS** (default-on, review key 2). C2 meteors → PASS.
> - **Sky-color ownership debt → RESOLVED** (documentation trap, not a bug; ownership law now in `ComposeLighting`).
> - **#5 shadows → the TERRAIN/CDLOD chat owns the remaining (mesh-coupled) work now**; sky-side config is the stable
>   reference (`handoffs/2026-06-21-shadow-terrain-coordination.md`).
>
> **What actually remains in the sky/light lane (ranked):** (1) structural debt — extract `LightingComposer` /
> `SkySubsystems` from the `TerrainLabUI` god-class + a `SkyMaterial` facade from `CloudVolume`; (2) **C3 N-suns /
> N-moons** (the last big feature — needs the luminary-abstraction refactor of `ComposeLighting` + `cloud_sky.gdshader`,
> so fold #1 into it rather than refactoring twice); (3) **#7 end-of-arc code-efficiency pass — LAST**, after all sky
> work is gated. Optional small: meteor/night presets.

---

## NEXT (priority order) — ⚠ SUPERSEDED, see the delta box above. Kept for the failure-analysis only.

### 1. ⭐ Galaxy / Nebula — START OVER (fresh concept). ❌ OBSOLETE: this was tried (C1-v2) and KILLED. See delta box.
The C1 procedural-noise approach was REJECTED. Do NOT iterate it again — **brainstorm a genuinely different
visual direction from scratch.** This is a fresh spec → plan → build, learning from the failure below.

**Why the C1 approach failed (capture so v2 doesn't repeat it):**
- It was **fbm/ridged 3D noise** — i.e. *the same noise the actual clouds use* → it kept reading as **clouds**,
  not space. Sharpening/finer-frequency/ridged-filaments helped but never escaped the cloud feel.
- A great-circle band read as a **"fog band across the whole world"** → localizing to a patch helped but then it
  was "all in one place."
- A `pow(cd)` core bulge **bloomed into a soft glow** the user disliked.
- Net: lots of knobs, but it never read as a believable/cool fantasy galaxy or nebula.
- **C1-v2 addendum:** the billboard redo (structured generator, domain-warp, volumetric, lit volumetric) ALSO
  failed within budget → feature removed. Only authored textures or >1 ms lit volumetrics could clear the bar.

## Run / coordination (unchanged)
- One Godot at a time: kill strays `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Always
  `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`. Build `dotnet build WG16.csproj`.
- **Night review: key 2** ("night sky — stars + moon + meteors"); it drives night directly (robust to the
  ground-strip `Apply.cs` change). For headless captures crop to the sky region (the lab panel + terrain fill the
  rest). Godot exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/.../Godot_v4.6.2-stable_mono_win64.exe`.
- **Shared branch `experiment/presentation` with the ground chat** — they STRIPPED the material/surfacing system
  to a placeholder 2026-06-21 (deleted Splat/Height/GroundReview etc.; the ground is now a height-color placeholder
  pending a CDLOD/infinite-terrain arc). Stay in sky/light + cloud files; `git add` paths explicitly, never `-A`.
- Commit-by-default; push when asked.

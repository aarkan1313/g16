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

## NEXT (priority order)

### 1. ⭐ Galaxy / Nebula — START OVER (fresh concept). User's call: "start over on the nebulae and galaxy."
The C1 procedural-noise approach was REJECTED. Do NOT iterate it again — **brainstorm a genuinely different
visual direction from scratch.** This is a fresh spec → plan → build, learning from the failure below.

**Why the C1 approach failed (capture so v2 doesn't repeat it):**
- It was **fbm/ridged 3D noise** — i.e. *the same noise the actual clouds use* → it kept reading as **clouds**,
  not space. Sharpening/finer-frequency/ridged-filaments helped but never escaped the cloud feel.
- A great-circle band read as a **"fog band across the whole world"** → localizing to a patch helped but then it
  was "all in one place."
- A `pow(cd)` core bulge **bloomed into a soft glow** the user disliked.
- Net: lots of knobs, but it never read as a believable/cool fantasy galaxy or nebula.

**Fresh directions to explore in the brainstorm (NOT decided — options):**
- **Painted/authored texture** — a hand-made or offline-generated galaxy/nebula image (or a few) sampled as a
  sky layer, instead of live procedural noise. Lets it actually look like art, not noise. (Cheapest runtime; the
  "look" is in the asset, not the math.)
- **Structured generator, not noise** — e.g. logarithmic spiral arms for a galaxy, or domain-warped emission with
  embedded bright stars + hard dust lanes, tuned to read as space (high dynamic range, sharp filaments, stars IN
  the gas). The point: a different *generator*, not the cloud fbm.
- **Distant-galaxy billboards** — a few small bright galaxy/nebula "objects" scattered across the sky as sprites
  (like the meteors are objects), rather than one big field. Fits the moon+stars sky as accents.
- **Drop it** — moon + stars + meteors may simply be enough; revisit only if a concept excites.
- Decide the **reference look first** (find/agree on what "good" is — a specific image) before building, so we're
  not iterating blind like last time.

**Infra that already exists (reusable):** the render-thread bake seam (`AtmosphereCompute` `_mw*` /
`night_sky_bake.glsl` / `Texture2Drd`) if v2 wants a baked texture; the orphaned **night-sky preset picker**
(`TerrainLabUI.NightSkyPresets.cs` + `data/night_sky_presets.json` + `--nspreset`) — currently galaxy presets,
repurposable. The galaxy/nebula shader code in `cloud_sky.gdshader` (`galaxy_color`/`nebula_color`, off at
brightness 0) can be deleted or replaced when v2 lands.

### 2. Meteors polish (optional, small)
Offered, not yet built: **meteor presets** (repurpose the night-sky picker into night-look presets — Calm Night /
Meteor Shower / Cosmic Fantasy / Fireballs — bundling meteor + star settings). Plus any head-glow/color tuning.
Low priority; do if wanted.

### 3. C2 continued — planets
Bright slow-moving points/discs; optional brighter named-star accents. (After/alongside the galaxy redo.)

### 4. #5 Shadow & Lighting pass
Contact + soft (PCSS) shadows, CSM/cascade tuning, the proxy cheap-shadow perf lever, SSIL re-check.

### 5. ⚙️ #7 End-of-arc code-efficiency pass — LAST
Non-tuning perf sweep over the whole lighting/cloud/sun/atmosphere lane (user: "look at code efficiency, not
tuning"). Only after the above are gated.

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

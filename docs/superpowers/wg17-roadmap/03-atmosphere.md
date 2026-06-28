# Slice B — Atmosphere  🟢 SHIPPED  (2026-06-28)

**What:** physically-based sky via the Hillaire scattering model — transmittance / multiple-scattering /
sky-view LUTs (sky color) + a log-Z aerial-perspective froxel (distance haze) + the 3 cloud-light colors.

**Docs:**
- Spec: [../specs/2026-06-28-wg17-sliceB-atmosphere-design.md](../specs/2026-06-28-wg17-sliceB-atmosphere-design.md) (in WG17 repo: `docs/superpowers/specs/`)
- Plan: WG17 `docs/superpowers/plans/2026-06-28-wg17-sliceB-atmosphere-plan.md`
- Kickoff: [../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md](../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md) (Slice B block)

**Outcome — it was a PORT, not a from-scratch rewrite.** The earlier premise here ("WG16 atmosphere never
validated, AT-2 a full-screen blue band → rebuild fresh") did NOT hold up on inspection: WG16's
`AtmosphereCompute` + the 5 `.glsl` shaders were sound, and the aerial screen shader already gates the sky
correctly (`sky_depth_eps` early-out on background depth — no blue band). So Slice B ported the proven code
byte-exact and **validated it numerically** with `--atmoscheck` (which is what was missing in WG16). Discipline
(user-directed): **"just modular" — keep capability, seam the coupling.** Nothing cut; full capability kept
(LUTs, aerial, extra-sun scattering, cloud-light extractor); only cross-module coupling rewritten to seams.

**Built (WG17 `src/atmosphere/` + `src/app/`):** `Std430Writer`, `AtmosphereCompute` (`IAtmosphereFeed`),
`AerialPerspectiveV2`, `IAtmosphereFeed`, `checks/AtmosphereCheck` (extracted from the compute class),
`shaders/atmosphere_sky.gdshader` (minimal LUT-sampling sky — Slice C's cloud sky supersedes it),
`src/app/AtmosphereDriver` (the seam host: `ILuminaryFeed` in, owns the nodes, swaps the Sky material).

**Reads:** `ILuminaryFeed` (sun + extra-suns from the LuminaryBudget). **Publishes:** `IAtmosphereFeed`
(sky-view + aerial textures + 3 cloud-light colors) → Clouds. **GPU rule:** LUT `Texture2Drd`/`Texture3Drd`
assigned once (bug #118292); aerial node's `AddChild` deferred until its RID is live (fixed a sampler-validate
race).

**Gates:** `--atmoscheck` PASS — transmittance ∈[0,1], skyview `horizonLuma 0.159 > zenith 0.017` (real
Rayleigh), `CLOUDLIGHTCHECK maxdiff=0.000000`. **Eye-gate PASS** (physical sky dawn→dusk, aerial hazes
distance, world-locked). **Profile: 4.18ms** atmo+aerial ON vs 4.17ms baseline — free at steady state (LUTs
cached; rebuild amortized to sun-move).

**Deferred to Slice C (NOT cut):** the real sun + moon **discs** (limb/corona/phase/maria) live in the cloud
sky shader; the cloud-light extractor is exposed via `IAtmosphereFeed`, dormant until clouds consume it.

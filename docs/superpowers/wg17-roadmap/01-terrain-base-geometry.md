# Slice 1 — Base Geometry (Field + CDLOD)  🟢 SHIPPED

**What:** infinite procedural heightfield (analytic field) rendered via CDLOD — quadtree LOD, pooled chunk
streaming, GPU per-chunk height+normal cache, floating-origin snap. The foundation everything renders on.

**Docs:**
- Spec: [../specs/2026-06-28-wg17-terrain-slice-design.md](../specs/2026-06-28-wg17-terrain-slice-design.md)
- Plan: [../plans/2026-06-28-wg17-terrain-base-geometry.md](../plans/2026-06-28-wg17-terrain-base-geometry.md)
- Kickoff: [../plans/2026-06-28-wg17-KICKOFF-PROMPT.md](../plans/2026-06-28-wg17-KICKOFF-PROMPT.md)

**Key seam introduced:** `IHeightSource` (`ProducePage(originX, originZ, spacing, res, fieldMode) → float[]`)
— decouples height consumers from `FieldCompute`. Water reuses this verbatim later.

**Outcome (SHIPPED):** flying **4.2ms** (WG16 was 7.2ms), 6 self-checks PASS (Field determinism, Stream,
Morph, Stitch, Pop, Snap). Shadowless by design — terrain casts no shadows; shadow ownership belongs to the
Lighting slice's `ShadowRegistry`. Early "hilltop black spots" were CSM acne, correctly resolved by the
shadowless decision (not a terrain bug).

**Fragile contract (guarded):** the shader/std430 uniform contract — `ground.gdshader` field uniforms,
`field_math.gdshaderinc` spliced 3 ways, the 128-byte std430 `ParamsBuf`. `FieldCheck` (determinism) guards it.

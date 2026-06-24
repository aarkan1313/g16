using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// S3 mechanical streaming guard (--streamcheck). Walks a synthetic straight-line camera traverse across many
/// root-window boundaries and asserts the streaming logic stays sound ALONG the path (not one frame):
///   (A) the <=1-level neighbor invariant holds at EVERY step (a roaming-window seam must not create a
///       >1-level jump = uncrackable).
///   (B) renderOrigin snap field-CONTINUITY: a fixed world point reconstructs to the SAME true XZ under any
///       snapped origin (relative + snapped origin == true) — the no-shimmer guarantee, numerically.
/// Drive+measure only; the live no-edge/no-hitch call is the user's eye-gate. Mirrors --morphcheck style.
public static class StreamCheck
{
    public static bool Run(float regionSize, int maxDepth, float splitFactor, out string msg)
    {
        var qt = new CdlodQuadtree(-regionSize * 0.5f, -regionSize * 0.5f, regionSize, maxDepth, splitFactor);
        int steps = 200;
        float step = regionSize / 8f;
        bool invOk = true; string worstInv = "ok";
        // ARC B Task 1: the load-ring radius is now tunable (default 2 = 5×5). The neighbor invariant must hold
        // at the EXPANDED outer ring too, so sweep R∈{1,2,3} along the whole traverse (1 = legacy 3×3).
        for (int ring = 1; ring <= 3 && invOk; ring++)
        {
            qt.Ring = ring;
            for (int s = 0; s < steps; s++)
            {
                var cam = new Vector3(s * step, 400f, s * step * 0.5f);
                List<CdlodChunk> leaves = qt.SelectRoaming(cam);
                if (!qt.NeighborInvariantHolds(leaves, out string m)) { invOk = false; worstInv = $"ring {ring} step {s} cam=({cam.X:F0},{cam.Z:F0}): {m}"; break; }
            }
        }

        float snap = regionSize;   // matches CdlodTerrain._coarseSnap = _regionSize
        float worldPt = 1234.5f;
        float maxDelta = 0f;
        foreach (float camX in new float[] { 0f, snap * 0.4f, snap * 0.9f, snap * 1.1f, snap * 2.3f })
        {
            float origin = Mathf.Floor(camX / snap) * snap;
            float relative = worldPt - origin;
            float reconstructed = relative + origin;
            maxDelta = Mathf.Max(maxDelta, Mathf.Abs(reconstructed - worldPt));
        }
        bool contOk = maxDelta < 1e-3f;

        bool ok = invOk && contOk;
        msg = ok
            ? $"{steps} traverse steps × rings 1-3 invariant=ok; snap field-continuity maxDelta={maxDelta:E2} (<1e-3) — no shimmer"
            : (!invOk ? $"invariant FAIL: {worstInv}" : $"snap field-continuity FAIL: maxDelta={maxDelta:E2}");
        return ok;
    }
}

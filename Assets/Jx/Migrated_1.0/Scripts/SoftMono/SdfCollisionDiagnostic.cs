// SdfCollisionDiagnostic.cs
// Attach to any GameObject in the scene.
// Each frame it prints a readable report into the Console showing
// exactly what the SDF sees for a test particle at a given world position,
// and for every registered SDF collider reports the contact state.
// This tells you definitively whether the problem is the baker data,
// the AABB reject, or the solve response.

using UnityEngine;

namespace XPBD
{
    public class SdfCollisionDiagnostic : MonoBehaviour
    {
        [Header("Test particle position (world space)")]
        public Transform TestParticle;          // drag any object here; its position is probed

        [Header("Probe settings")]
        public float ContactSkin = 0.02f;
        [Tooltip("How many radial directions to sweep for gradient sanity check")]
        public int   GradientDirs = 6;
        public bool  LogEveryFrame = false;     // turn on for continuous spam
        [Space]
        public KeyCode DiagnoseKey = KeyCode.F9; // press in play mode

        void Update()
        {
            if (Input.GetKeyDown(DiagnoseKey) || LogEveryFrame)
                RunDiagnostic();
        }

        [ContextMenu("Run Diagnostic Now")]
        public void RunDiagnostic()
        {
            var mgr = SoftBodySimulationManager.Instance;
            if (mgr == null)
            {
                Debug.LogError("[SdfDiag] No SoftBodySimulationManager in scene.");
                return;
            }

            // Use TestParticle position, or this transform if none assigned
            Vector3 worldPos = TestParticle != null
                ? TestParticle.position
                : transform.position;

            // Find all SDF colliders via manager's registered list (reflection fallback)
            var colliders = FindObjectsByType<XpbdSdfCollider>(FindObjectsSortMode.None);
            if (colliders.Length == 0)
            {
                Debug.LogWarning("[SdfDiag] No XpbdSdfCollider found in scene.");
                return;
            }

            Debug.Log($"[SdfDiag] ══════════════════════════════════════════");
            Debug.Log($"[SdfDiag] Test particle world pos: {worldPos:F4}");
            Debug.Log($"[SdfDiag] ContactSkin: {ContactSkin}");
            Debug.Log($"[SdfDiag] Checking {colliders.Length} SDF collider(s)...");

            foreach (var col in colliders)
            {
                if (col.SdfAsset == null || !col.SdfAsset.IsBaked)
                {
                    Debug.LogWarning($"[SdfDiag]   [{col.name}] NOT BAKED — skip");
                    continue;
                }

                var asset = col.SdfAsset;

                // ── 1. Count negative voxels (baker fix check) ────────────────────
                int negCount = 0;
                float minNeg = 0f, maxNeg = 0f;
                foreach (float v in asset.SdfGrid)
                {
                    if (v < 0f)
                    {
                        negCount++;
                        if (v < minNeg) minNeg = v;
                        if (v > maxNeg) maxNeg = v;
                    }
                }
                bool bakerFixed = negCount > 0;
                Debug.Log($"[SdfDiag]   [{col.name}] Baker check: " +
                          $"{negCount} negative voxels " +
                          (bakerFixed
                              ? $"✓ (range [{minNeg:F5}, {maxNeg:F5}])"
                              : "✗ ZERO NEGATIVES — re-bake with fixed SdfBaker.cs!"));

                // ── 2. World-space AABB check ─────────────────────────────────────
                col.RefreshDescriptor(0f);
                var desc = col.Descriptor;
                Vector3 delta = new Vector3(
                    Mathf.Abs(worldPos.x - desc.aabbCentre.x),
                    Mathf.Abs(worldPos.y - desc.aabbCentre.y),
                    Mathf.Abs(worldPos.z - desc.aabbCentre.z));
                Vector3 margin = desc.aabbExtents + Vector3.one * ContactSkin;
                bool aabbHit = delta.x <= margin.x && delta.y <= margin.y && delta.z <= margin.z;
                Debug.Log($"[SdfDiag]   [{col.name}] AABB broad phase: " +
                          $"centre={desc.aabbCentre:F3} extents={desc.aabbExtents:F3} " +
                          (aabbHit ? "✓ INSIDE" : "✗ OUTSIDE — particle rejected here!"));

                if (!aabbHit) continue;

                // ── 3. Local-space position and SDF sample ────────────────────────
                Vector3 localPos = col.transform.InverseTransformPoint(worldPos);
                float sdfVal = asset.SampleLocal(localPos);

                Debug.Log($"[SdfDiag]   [{col.name}] Local pos: {localPos:F4}  SDF: {sdfVal:F5}");

                string zone = sdfVal < 0f        ? "INSIDE WALL (negative) — contact should apply"
                            : sdfVal < ContactSkin ? $"SKIN ZONE (0 to {ContactSkin}) — vel damping only"
                            : sdfVal < 1e10f       ? "OUTSIDE (positive) — no contact"
                            : "OUT OF GRID BOUNDS — returns 1e38, no contact";
                Debug.Log($"[SdfDiag]   [{col.name}] Zone: {zone}");

                // ── 4. Gradient (contact normal) check ───────────────────────────
                if (sdfVal < ContactSkin && sdfVal < 1e10f)
                {
                    Vector3 gradLocal = GradientLocal(asset, localPos);
                    Vector3 gradWorld = col.transform.TransformDirection(gradLocal).normalized;

                    Debug.Log($"[SdfDiag]   [{col.name}] SDF gradient (local):  {gradLocal:F4}");
                    Debug.Log($"[SdfDiag]   [{col.name}] Contact normal (world): {gradWorld:F4}");

                    // Check if the normal makes geometric sense:
                    // It should point FROM the surface TOWARD the particle
                    // i.e. dot(nrm, worldPos - surfacePt) > 0
                    Vector3 surfacePt = worldPos - sdfVal * gradWorld;
                    float sanity = Vector3.Dot(gradWorld, worldPos - surfacePt);
                    Debug.Log($"[SdfDiag]   [{col.name}] Normal sanity " +
                              $"(dot(nrm, pos-surface) should be ≈ 0): {sanity:F5} " +
                              (Mathf.Abs(sanity) < 0.001f ? "✓" : "⚠ non-zero"));

                    // SolveCollisions plane test
                    float planeDist = Vector3.Dot(surfacePt, gradWorld);
                    float pen = Vector3.Dot(worldPos, gradWorld) - planeDist;
                    Debug.Log($"[SdfDiag]   [{col.name}] SolveCollisions pen = {pen:F5} " +
                              (pen < 0f ? "✓ CORRECTION will fire" :
                               pen < ContactSkin ? "skin zone (vel only)" : "outside"));
                    if (pen > 0f && sdfVal < 0f)
                        Debug.LogWarning($"[SdfDiag]   [{col.name}] ⚠ BUG: sdf<0 but pen>0 — " +
                            "wrong normal direction! Inner wall gradient flip issue.");

                    // ── 5. Gradient direction sweep ───────────────────────────────
                    Debug.Log($"[SdfDiag]   [{col.name}] Gradient h vs wall depth:");
                    float wallDepth = -minNeg;
                    Vector3 cs = asset.CellSize;
                    Debug.Log($"[SdfDiag]     cell = {cs:F5}  min(cell) = {Mathf.Min(cs.x,Mathf.Min(cs.y,cs.z)):F5}");
                    Debug.Log($"[SdfDiag]     wall SDF depth = {wallDepth*1000:F2}mm");
                    Debug.Log($"[SdfDiag]     gradient h (current) = cell = {Mathf.Max(cs.x,Mathf.Max(cs.y,cs.z))*1000:F2}mm");
                    if (Mathf.Max(cs.x, Mathf.Max(cs.y, cs.z)) > wallDepth * 1.5f)
                        Debug.LogWarning($"[SdfDiag]     ⚠ Gradient step > wall depth! " +
                            "Normal may be wrong at inner surface. " +
                            $"Use res≥{Mathf.CeilToInt(64 * 0.01f / wallDepth)} or reduce gradient h in shader.");
                }

                // ── 6. Bore interior check ────────────────────────────────────────
                // Sample the bore center to confirm it's positive (correct open bore)
                Vector3 boreCenter = col.transform.TransformPoint(
                    (asset.BoundsMin + asset.BoundsMax) * 0.5f);
                Vector3 boreCenterLocal = col.transform.InverseTransformPoint(boreCenter);
                float boreSdf = asset.SampleLocal(boreCenterLocal);
                Debug.Log($"[SdfDiag]   [{col.name}] Bore center SDF = {boreSdf:F5} " +
                          (boreSdf > 0 ? "✓ (bore is open/outside)" :
                           "✗ BORE IS NEGATIVE — bake issue!"));
            }
            Debug.Log($"[SdfDiag] ══════════════════════════════════════════");
        }

        static Vector3 GradientLocal(SdfColliderAsset asset, Vector3 p)
        {
            Vector3 h = asset.CellSize;
            float dx = asset.SampleLocal(p + new Vector3(h.x, 0, 0))
                     - asset.SampleLocal(p - new Vector3(h.x, 0, 0));
            float dy = asset.SampleLocal(p + new Vector3(0, h.y, 0))
                     - asset.SampleLocal(p - new Vector3(0, h.y, 0));
            float dz = asset.SampleLocal(p + new Vector3(0, 0, h.z))
                     - asset.SampleLocal(p - new Vector3(0, 0, h.z));
            var g = new Vector3(dx / (2f * h.x), dy / (2f * h.y), dz / (2f * h.z));
            return g.sqrMagnitude > 1e-10f ? g.normalized : Vector3.up;
        }
    }
}

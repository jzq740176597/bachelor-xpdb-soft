# XPBD Soft Body Physics — Unity Port — Reboot Context

## Project Summary
GPU-accelerated XPBD soft body physics, ported from C++/Vulkan to Unity 2022.3 Built-in RP, Windows DX12.  
All simulation runs on compute shaders. Rendering via `Graphics.DrawMesh` each frame.

---

## Code Formatting Rules (STRICT — User Preference)
- **Never** put if/else/loop body on the same line as the condition
- Single-statement bodies: **newline, no braces required** — `if (a)\n    return b;` is fine
- Braces **required** for: method bodies, property bodies, multi-statement blocks, else-if chains
- Never: `if (a) return b;` or `if (a) continue;` on one line
- Never: `void OnEnable() => expr;` — method bodies always need `{ }`

---

## Output Files (Current State)

| File | Purpose |
|------|---------|
| `SoftBodySim.compute` | Presolve, Postsolve, StretchConstraint, VolumeConstraint, ClampDelta, ClearDelta, StretchConstraintWithExclusion, VolumeConstraintWithExclusion |
| `Collision_Shapes.compute` | ClearImpulseAccum, DetectShapes, SolveCollisions, WriteCollisionState, ClearCollisionState, HardProjectParticles |
| `SoftBodySimulationManager.cs` | Main simulation loop, collision pipeline, adaptive skin/particle radius |
| `SoftBodyGPUState.cs` | GPU buffer ownership per body (readonly fields, DeltaBytesBuffer=Raw, CollisionStateBuffer) |
| `SoftBodyComponent.cs` | MonoBehaviour wrapper, exposes `TetMeshAsset`, `BoundingRadius`, `ParticleRadius` |
| `XpbdColliderSource.cs` | Kinematic/dynamic/static collider descriptor, per-substep sweep interpolation |
| `SoftParticleAttachment.cs` | Pin/spring particle groups to a Transform (Static/Dynamic) |
| `TetrahedralMeshAsset.cs` | ScriptableObject: Particles[], Edges[], Tetrahedrals[], Groups[] |
| `TetrahedralMeshAssetEditor.cs` | Scene editor: circle-select, paint groups, invMass editing |

---

## Architecture

### GPU Buffer Layout
```
Particle (32 bytes):   float3 position, float _pad, float3 velocity, float invMass
PbdPositions (32 bytes): float3 predict, float _pad, float3 delta(unused), float _pad
Edge (16 bytes):       uint2 indices, float restLen, float _pad
Tetrahedral (32 bytes): uint4 indices, float restVolume, float3 _pad
CollisionState (16 bytes): float4(normal.xyz, depth)  — depth<0=penetrating, >=1e9=no collision
```

### Delta Accumulation
- `_Delta`: `RWStructuredBuffer<float3>` — Presolve clears, constraints accumulate via CAS atomics
- `_DeltaRaw`: `RWByteAddressBuffer` bound to **same** ComputeBuffer — used for CAS atomic float adds
- Postsolve: `predict += _Delta[i]`, `velocity = (predict - position) / dt`

### Per-Substep Order (CRITICAL)
```
Presolve                          (clear _Delta, save position, integrate velocity)
ClearCollisionState               (reset all to depth=1e9)
DetectShapes + SolveCollisions    (CCD swept quadratic, push predict+position to surface)
WriteCollisionState               (mark boundary particles, pen = dist - (r + _ParticleRadius))
StretchConstraintWithExclusion    (eff=0 for penetrating particles — contact particles treated as pinned)
VolumeConstraintWithExclusion     (same)
ClampDelta                        (zero inward elastic delta for contact particles)
Postsolve
CollisionIterations × (DetectShapes + SolveCollisions)   [post-elastic passes]
HardProjectParticles              (ABSOLUTE LAST — analytic guarantee, no atomics)
```

---

## Key Algorithms

### CCD (Continuous Collision Detection) for Sphere
Swept segment-vs-moving-sphere quadratic:
- `rel0 = pStart - sStart` (particle and sphere at substep start)
- `relV = (pEnd - pStart) - (sEnd - sStart)` (relative velocity)
- Solve `a*t²+b*t+c=0` for earliest `t ∈ [0,1]`
- `c < 0` = already inside at t=0 → immediate contact
- CPU encodes `sStart` into `ShapeDescriptor.axis` (unused for sphere)

### SolveCollisions (Sphere mode)
- `contactPt = sphereEndCentre`, `sphereRadius = r` (stored in constraint)
- Re-evaluate: `pen = dist(predict, contactPt) - sphereRadius`
- `pen < 0`: push `predict` AND `position` to surface (breaks stable equilibrium)
- Kill inward velocity component always

### HardProjectParticles
- Thread per particle, inner loop over all shapes
- Sphere: `rExclude = r + _ParticleRadius`, `pen = dist - rExclude`
- If `pen < 0`: `pos = centre + nrm * rExclude` (project centre to exclusion zone boundary)
- Writes `predict`, `velocity`, `position` — nothing runs after this

### StretchConstraintWithExclusion
- `eff0 = (CollisionState[i0].w < 0.0) ? 0.0 : invMass`  ← only truly penetrating
- `w = eff0 + eff1` — if both 0, skip (both pinned, cannot move)
- Correction applied only to non-zero eff particles
- Contact particles act as infinite-mass — elastic forces cannot push them inward

### Particle Radius (Mesh-Data-Driven)
```csharp
// Per particle: half mean of connected edge rest lengths
for each edge (A,B) with restLen:
    edgeLenSum[A] += restLen; edgeCount[A]++
    edgeLenSum[B] += restLen; edgeCount[B]++
perParticleR[i] = (edgeLenSum[i] / edgeCount[i]) * 0.5f
medianR = median(perParticleR)
body.ParticleRadius = medianR * ParticleRadiusScale   // default 0.8
```
Then: `_particleRadius = max(body.ParticleRadius, minGripRadius)` — small grips force large enough contact patch.

### XpbdColliderSource — Per-Substep Sweep
- `SnapshotStartOfFrame()` captures `_prevPos` before `RebuildCollisionBuffers`
- `RefreshDescriptorAtFraction(t, subDT, slot)`: lerps centre from frame start to end
- Kinematic: uses `Body.position` (not `transform.position` — avoids `MovePosition` lag)
- Sphere encodes `_prevSubstepCentre` into `descriptor.axis` for GPU CCD
- `SurfaceVelocity = (lerpPos - _prevPos) / subDT` — per-substep velocity

### Adaptive Contact Skin
```
_particleRadius = max(medianEdgeRadius * ParticleRadiusScale, minGripRadius)
_contactSkin    = clamp(bodyExtent * ContactSkinFraction, SkinMin, SkinMax)
// Skin and particleRadius are INDEPENDENT — skin stays small for velocity detection
```

---

## Key Inspector Fields (SoftBodySimulationManager)

| Field | Default | Purpose |
|-------|---------|---------|
| `SubSteps` | 20 | Substeps per fixed frame |
| `EdgeCompliance` | 0.01 | Stretch stiffness (0=rigid) |
| `VolumeCompliance` | 0.0 | Volume stiffness |
| `CollisionIterations` | 1 | Post-elastic detect+solve passes |
| `UseExclusionStretch` | true | Enable Stretch/Volume exclusion |
| `ExclusionIterations` | 1 | Times to re-run exclusion constraints |
| `ParticleRadiusScale` | 0.8 | Scale on mesh-derived particle radius |
| `ContactSkinMin/Max` | 0.005/0.10 | Skin clamp range |
| `ContactSkinFraction` | 0.25 | Skin = bodyRadius * fraction |

---

## Known Bugs Fixed (Chronological)

1. **WaveActiveSum CCD explosion** — wave intrinsics wrong for general mesh, replaced with CAS atomics
2. **`_Delta` buffer name mismatch** — `_DeltaBytes` → `_Delta` + `_DeltaRaw` dual binding
3. **Presolve invMass guard** — gravity applied to pinned particles, fixed with early return
4. **`SolveCollisions` stale plane test** — post-Stretch predict mismatch, fixed with sphere re-eval from `contactPt` (sphere centre)
5. **Stable penetration equilibrium** — elastic velocity not zeroed, fixed by writing `position = newPredict`
6. **`c < 0` CCD branch** — was picking exit root when inside, fixed to return immediate contact
7. **Snapshot ordering** — `SnapshotStartOfFrame` called after `RefreshDescriptor` so start==end, no sweep. Fixed: snapshot BEFORE rebuild
8. **`transform.position` lag for kinematic** — `rb.MovePosition` deferred, fixed with `Body.position`
9. **Magnet/glue effect** — `SolveCollisions` was positionally correcting skin-zone (outside) particles inward. Fixed: `pen >= 0 → velocity only, no position`
10. **`WriteCollisionState` always no-op** — was evaluating pen after SolveCollisions corrected it to ~0. Fixed: use `pen < _ContactSkin` threshold
11. **Explosion from delta injection** — `_CollisionDeltaWeight * correction` into `_Delta` caused huge values. Removed entirely, HardProject is sufficient
12. **Partial explosion from exclusion** — `eff=0` for `depth < _ContactSkin` pinned entire shells. Fixed: `eff=0` only for `depth < 0` (actual penetration)
13. **ExclusionIterations doubling delta** — multiple Stretch runs without clearing `_Delta`. Fixed: `ClearDelta` kernel between iterations
14. **`HardProject` magnet** — projected to `r` not `rExclude`, pulled outside particles inward. Fixed: `pos = centre + nrm * rExclude`

---

## Current Penetration Status
- Large grips (R ≥ 0.3): robust, no penetration under normal drag speed
- Small grips (R = 0.1): significantly improved. `_particleRadius = max(meshEdgeRadius, gripRadius)` ensures contact patch is always large enough
- Very fast drag still possible to partially penetrate — `CollisionIterations` and `ExclusionIterations` can be raised at GPU cost
- `HardProjectParticles` provides absolute last-resort guarantee per substep

---

## Pending Items
- [ ] Cache `_originalInvMass[]` in SoftParticleAttachment for clean Static detach
- [ ] AsyncGPUReadback for SoftParticleAttachment with large groups
- [ ] Restitution coefficient (currently perfectly inelastic)
- [ ] Broad-phase for >5 bodies: BVH or spatial hash
- [ ] Runtime test: soft-soft collision with two spheres
- [ ] CCD for capsule (currently static skin only)

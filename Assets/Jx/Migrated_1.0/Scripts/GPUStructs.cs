// GPUStructs.cs
// C# GPU struct definitions — layout must match the HLSL structs exactly.
// All structs use [StructLayout(LayoutKind.Sequential)] with explicit padding
// to replicate std140 alignment (alignas(16) in the original C++ headers).
//
// Rule of thumb from the C++ source:
//   vec3/glm::vec3  with alignas(16) → float3 + 1 float pad  = 16 bytes
//   float           alone            → no extra pad needed
//
// Validate with: new ComputeBuffer(1, Marshal.SizeOf<T>())
// and check size matches HLSL sizeof(T).

using System.Runtime.InteropServices;
using UnityEngine;

namespace XPBD
{
	// ─── Particle ─────────────────────────────────────────────────────────────
	// GLSL: vec3 position (alignas 16), vec3 velocity (alignas 16), float invMass
	// C++ : alignas(16) glm::vec3 position; alignas(16) glm::vec3 velocity; float invMass;
	// Size: 32 bytes  (position 16 + velocity 12 + invMass 4)
	[StructLayout(LayoutKind.Sequential)]
	public struct GPUParticle
	{
		public Vector3 position;  // 12 bytes
		public float _pad0;     //  4 bytes → 16 aligned
		public Vector3 velocity;  // 12 bytes
		public float invMass;   //  4 bytes → 32 bytes total
	}

	// ─── PbdPositions ─────────────────────────────────────────────────────────
	// GLSL: vec3 predict, vec3 delta (both std140 padded)
	// Size: 32 bytes
	[StructLayout(LayoutKind.Sequential)]
	public struct GPUPbdPositions
	{
		public Vector3 predict;   // 12 bytes
		public float _pad0;     //  4 bytes → 16 aligned
		public Vector3 delta;     // 12 bytes
		public float _pad1;     //  4 bytes → 32 bytes total
	}

	// ─── Edge ─────────────────────────────────────────────────────────────────
	// GLSL: uvec2 indices (alignas 16), float restLen
	// C++ : alignas(16) glm::uvec2; alignas(4) float
	// Size: 16 bytes
	[StructLayout(LayoutKind.Sequential)]
	public struct GPUEdge
	{
		public uint indexA;   //  4 bytes
		public uint indexB;   //  4 bytes
		public float restLen;  //  4 bytes
		public float _pad;     //  4 bytes → 16 bytes total
	}

	// ─── Tetrahedral ──────────────────────────────────────────────────────────
	// GLSL: uvec4 indices (16 bytes), float restVolume (4 bytes + 12 pad)
	// C++ : alignas(16) glm::uvec4; alignas(4) float
	// Size: 32 bytes
	[StructLayout(LayoutKind.Sequential)]
	public struct GPUTetrahedral
	{
		public uint i0, i1, i2, i3;  // 16 bytes (uvec4)
		public float restVolume;       //  4 bytes
		public float _pad0, _pad1, _pad2; // 12 bytes padding → 32 bytes total
	}

	// ─── ColConstraint ────────────────────────────────────────────────────────
	// C++ : alignas(16) glm::vec3 orig; alignas(4) uint32_t particleIndex;
	//       alignas(16) glm::vec3 normal;
	// Size: 32 bytes
	[StructLayout(LayoutKind.Sequential)]
	public struct GPUColConstraint
	{
		public Vector3 orig;          // 12 bytes
		public uint particleIndex; //  4 bytes → 16 aligned
		public Vector3 normal;        // 12 bytes
		public float _pad;          //  4 bytes → 32 bytes total
	}

	// ─── SkinningInfo ─────────────────────────────────────────────────────────
	// GLSL: vec3 weights, uint tetIndex
	// Size: 16 bytes
	[StructLayout(LayoutKind.Sequential)]
	public struct GPUSkinningInfo
	{
		public Vector3 weights;   // 12 bytes  (bary coords for tet verts 0,1,2)
		public uint tetIndex;  //  4 bytes → 16 bytes total
							   // w4 = 1 - (weights.x + weights.y + weights.z) computed in shader
	}

	// ─── Sizes (bytes) — for ComputeBuffer stride ─────────────────────────────
	public static class GPUStrides
	{
		public static readonly int Particle = Marshal.SizeOf<GPUParticle>();      // 32
		public static readonly int PbdPositions = Marshal.SizeOf<GPUPbdPositions>();  // 32
		public static readonly int Edge = Marshal.SizeOf<GPUEdge>();          // 16
		public static readonly int Tetrahedral = Marshal.SizeOf<GPUTetrahedral>();   // 32
		public static readonly int ColConstraint = Marshal.SizeOf<GPUColConstraint>(); // 32
		public static readonly int SkinningInfo = Marshal.SizeOf<GPUSkinningInfo>();  // 16
	}
}

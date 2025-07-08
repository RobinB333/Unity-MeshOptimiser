using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Evergine.Bindings.MeshOptimizer;


namespace Ragnox.MeshOptimizer
{
    /// <summary>
    /// High-level static API that turns any Unity Mesh into a simplified copy
    /// while keeping attributes and topology valid.
    /// </summary>
    public static unsafe class MeshOptimizerUnity
    {
        /// <summary>
        /// Simplify <paramref name="sourceMesh"/> to roughly
        /// <paramref name="targetTriangleRatio"/> of its original triangle
        /// count while respecting <paramref name="targetError"/> (relative).
        /// Returns a *new* <see cref="UnityEngine.Mesh"/>; the original is
        /// untouched.
        /// </summary>
        /// <exception cref="ArgumentNullException"/>
        /// <exception cref="ArgumentOutOfRangeException"/>
        /// <exception cref="InvalidOperationException"/>
        public static Mesh DecimateMesh(
                Mesh sourceMesh,
                Single targetTriangleRatio = 0.5f,
                Single targetError = 0.01f)
        {
            if (sourceMesh == null) { throw new ArgumentNullException(nameof(sourceMesh)); }
            if (targetTriangleRatio <= 0.0f || targetTriangleRatio > 1.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(targetTriangleRatio),
                        "targetTriangleRatio must be in (0,1].");
            }

            /* ───────────────────── gather original data ───────────────────── */

            Vector3[] original_vertexArray = sourceMesh.vertices;
            Int32[] original_indicesIntArr = sourceMesh.triangles;

            Int32 original_indexCount = original_indicesIntArr.Length;
            Int32 original_vertexCount = original_vertexArray.Length;

            if (original_indexCount == 0 || original_vertexCount == 0)
            {
                throw new InvalidOperationException("Mesh must contain geometry.");
            }


            UInt32[] original_indicesUIntArr = new UInt32[original_indexCount];
            for (Int32 indexPosition = 0; indexPosition < original_indexCount; indexPosition++)
            {
                original_indicesUIntArr[indexPosition] =
                        unchecked((UInt32)original_indicesIntArr[indexPosition]);
            }

            Int32 target_indexCount = Mathf.Max(3,
                    (Int32)(original_indexCount * Mathf.Clamp01(targetTriangleRatio)));
            target_indexCount -= target_indexCount % 3;            /* keep full triangles */
            if (target_indexCount < 3) { target_indexCount = 3; }

            UInt32[] simplified_indicesUIntArr = new UInt32[original_indexCount];
            UIntPtr result_indexCount;

            fixed (UInt32* simplified_indicesPtr = simplified_indicesUIntArr)
            fixed (UInt32* original_indicesPtr = original_indicesUIntArr)
            fixed (Vector3* vertexPositionsPtr = original_vertexArray)
            {
                result_indexCount = MeshOptNative.meshopt_simplify(
                        simplified_indicesPtr,
                        original_indicesPtr,
                        (UIntPtr)original_indexCount,
                        (float*)vertexPositionsPtr,
                        (UIntPtr)original_vertexCount,
                        (UIntPtr)sizeof(Vector3),
                        (UIntPtr)target_indexCount,
                        targetError,
                        0u,
                        null);
            }

            Int32 simplified_indexCount = (Int32)result_indexCount;
            if (simplified_indexCount < 3)
            {
                throw new InvalidOperationException("Simplification failed (less than one triangle).");
            }

            /* Trim index buffer to actual result length */
            Array.Resize(ref simplified_indicesUIntArr, simplified_indexCount);

            /* ───────────────── compact vertex buffer ───────────────── */

            UInt32[] remap_oldToNewArr = new UInt32[original_vertexCount];
            UIntPtr unique_vertexCount;

            fixed (UInt32* remapPtr = remap_oldToNewArr)
            fixed (UInt32* simplifiedIdxPtr = simplified_indicesUIntArr)
            {

                unique_vertexCount = MeshOptNative.meshopt_optimizeVertexFetchRemap(
                        remapPtr,
                        simplifiedIdxPtr,
                        (UIntPtr)simplified_indexCount,
                        (UIntPtr)original_vertexCount);

                MeshOptNative.meshopt_remapIndexBuffer(
                        simplifiedIdxPtr,
                        simplifiedIdxPtr,
                        (UIntPtr)simplified_indexCount,
                        remapPtr);
            }

            Int32 compact_vertexCount = (Int32)unique_vertexCount;

            /* Build new packed vertex array */
            Vector3[] compact_vertexArray = new Vector3[compact_vertexCount];
            fixed (Vector3* compactVertPtr = compact_vertexArray)
            fixed (Vector3* originalVertPtr = original_vertexArray)
            fixed (UInt32* remapPtrRO = remap_oldToNewArr)
            {
                MeshOptNative.meshopt_remapVertexBuffer(
                        compactVertPtr,
                        originalVertPtr,
                        (UIntPtr)original_vertexCount,
                        (UIntPtr)sizeof(Vector3),
                        remapPtrRO);
            }

 
            Int32[] remap_newToOldArr = new Int32[compact_vertexCount];
            for (Int32 oldVertexIndex = 0; oldVertexIndex < original_vertexCount; oldVertexIndex++)
            {
                UInt32 newVertexIndex = remap_oldToNewArr[oldVertexIndex];
                if (newVertexIndex != UInt32.MaxValue) { remap_newToOldArr[newVertexIndex] = oldVertexIndex; }
            }

            /* Convert indices back to Int32 for Unity */
            Int32[] simplified_indicesIntArr = new Int32[simplified_indexCount];
            for (Int32 idx = 0; idx < simplified_indexCount; idx++)
            {
                simplified_indicesIntArr[idx] = unchecked((Int32)simplified_indicesUIntArr[idx]);
            }

            /* ───────────────── build new Mesh ───────────────── */

            Mesh decimatedMesh = new Mesh();
            decimatedMesh.indexFormat =
                    compact_vertexCount > UInt16.MaxValue ?
                    UnityEngine.Rendering.IndexFormat.UInt32 :
                    UnityEngine.Rendering.IndexFormat.UInt16;

            decimatedMesh.vertices = compact_vertexArray;
            decimatedMesh.triangles = simplified_indicesIntArr;

            /* Copy supported attributes */
            CopyAttributeIfPresent(sourceMesh.normals,
                    original_vertexCount, compact_vertexCount,
                    remap_newToOldArr, out Vector3[] compact_normalsArr);
            if (compact_normalsArr != null) { decimatedMesh.normals = compact_normalsArr; }

            CopyAttributeIfPresent(sourceMesh.tangents,
                    original_vertexCount, compact_vertexCount,
                    remap_newToOldArr, out Vector4[] compact_tangentsArr);
            if (compact_tangentsArr != null) { decimatedMesh.tangents = compact_tangentsArr; }

            CopyAttributeIfPresent(sourceMesh.uv,
                    original_vertexCount, compact_vertexCount,
                    remap_newToOldArr, out Vector2[] compact_uvArr);
            if (compact_uvArr != null) { decimatedMesh.uv = compact_uvArr; }

            CopyAttributeIfPresent(sourceMesh.uv2,
                    original_vertexCount, compact_vertexCount,
                    remap_newToOldArr, out Vector2[] compact_uv2Arr);
            if (compact_uv2Arr != null) { decimatedMesh.uv2 = compact_uv2Arr; }

            /* Generate normals if missing */
            if (decimatedMesh.normals == null || decimatedMesh.normals.Length == 0)
            {
                decimatedMesh.RecalculateNormals();
            }
            decimatedMesh.RecalculateBounds();

            return decimatedMesh;
        }

        private static void CopyAttributeIfPresent<T>(
                T[] source_attributeArr,
                Int32 original_vertexCount,
                Int32 compact_vertexCount,
                Int32[] remap_newToOldArr,
                out T[] compact_attributeArr) where T : struct
        {
            if (source_attributeArr != null && source_attributeArr.Length == original_vertexCount)
            {
                compact_attributeArr = new T[compact_vertexCount];
                for (Int32 newVertexIndex = 0; newVertexIndex < compact_vertexCount; newVertexIndex++)
                {
                    Int32 oldVertexIndex = remap_newToOldArr[newVertexIndex];
                    compact_attributeArr[newVertexIndex] = source_attributeArr[oldVertexIndex];
                }
            }
            else
            {
                compact_attributeArr = null;
            }
        }
    }

}
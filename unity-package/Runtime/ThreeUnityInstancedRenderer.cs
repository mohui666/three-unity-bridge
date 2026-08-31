using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    public sealed class ThreeUnityInstancedRenderer : MonoBehaviour
    {
        private const int MaxBatchSize = 1023;
        private static readonly int InstanceColorId = Shader.PropertyToID("_ThreeUnityInstanceColor");

        [SerializeField] private Mesh mesh;
        [SerializeField] private Material[] materials = Array.Empty<Material>();
        [SerializeField] private Matrix4x4[] localMatrices = Array.Empty<Matrix4x4>();
        [SerializeField] private Color[] instanceColors = Array.Empty<Color>();

        private Matrix4x4[][] localMatrixBatches = Array.Empty<Matrix4x4[]>();
        private Matrix4x4[][] worldMatrixBatches = Array.Empty<Matrix4x4[]>();
        private Vector4[][] colorBatches = Array.Empty<Vector4[]>();
        private MaterialPropertyBlock[] colorPropertyBlocks = Array.Empty<MaterialPropertyBlock>();
        private Matrix4x4[] batchedMatrixSource;
        private Color[] batchedColorSource;
        private Material[] readOnlyMaterialSource;
        private IReadOnlyList<Material> readOnlyMaterials = Array.Empty<Material>();
        private Matrix4x4 cachedLocalToWorld;
        private bool hasCachedLocalToWorld;
        private bool platformSupportChecked;
        private bool platformSupportsInstancing;
        private bool capabilityErrorLogged;

        public int InstanceCount => localMatrices == null ? 0 : localMatrices.Length;
        public int BatchCount => (InstanceCount + MaxBatchSize - 1) / MaxBatchSize;
        public Mesh SharedMesh => mesh;

        public IReadOnlyList<Material> SharedMaterials
        {
            get
            {
                RefreshReadOnlyMaterials();
                return readOnlyMaterials;
            }
        }

        public void Initialize(
            Mesh sharedMesh,
            Material[] sharedMaterials,
            Matrix4x4[] importedLocalMatrices,
            Color[] importedInstanceColors)
        {
            if (sharedMesh == null) throw new ArgumentNullException(nameof(sharedMesh));
            if (sharedMaterials == null) throw new ArgumentNullException(nameof(sharedMaterials));
            if (importedLocalMatrices == null) throw new ArgumentNullException(nameof(importedLocalMatrices));
            if (importedInstanceColors == null) throw new ArgumentNullException(nameof(importedInstanceColors));
            if (sharedMaterials.Length != sharedMesh.subMeshCount)
            {
                throw new ArgumentException(
                    $"Mesh '{sharedMesh.name}' has {sharedMesh.subMeshCount} submeshes, but {sharedMaterials.Length} materials were supplied.",
                    nameof(sharedMaterials));
            }
            for (var index = 0; index < sharedMaterials.Length; index++)
            {
                if (sharedMaterials[index] == null)
                    throw new ArgumentException($"Material slot {index} for mesh '{sharedMesh.name}' is null.", nameof(sharedMaterials));
            }
            if (importedInstanceColors.Length != 0 && importedInstanceColors.Length != importedLocalMatrices.Length)
            {
                throw new ArgumentException(
                    $"Instance color count {importedInstanceColors.Length} must be zero or match matrix count {importedLocalMatrices.Length}.",
                    nameof(importedInstanceColors));
            }

            mesh = sharedMesh;
            materials = (Material[])sharedMaterials.Clone();
            localMatrices = (Matrix4x4[])importedLocalMatrices.Clone();
            instanceColors = (Color[])importedInstanceColors.Clone();
            RebuildBatches();
        }

        public Matrix4x4 GetLocalMatrix(int index)
        {
            ValidateInstanceIndex(index);
            return localMatrices[index];
        }

        public Color GetInstanceColor(int index)
        {
            ValidateInstanceIndex(index);
            return instanceColors.Length == 0 ? Color.white : instanceColors[index];
        }

        private void OnEnable()
        {
            EnsureRuntimeState();
            if (Application.isPlaying) CheckPlatformSupport();
        }

        private void LateUpdate()
        {
            if (!platformSupportChecked) CheckPlatformSupport();
            if (!CanDraw()) return;

            EnsureRuntimeState();
            UpdateWorldMatrices();
            for (var submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
            {
                for (var batchIndex = 0; batchIndex < worldMatrixBatches.Length; batchIndex++)
                {
                    var worldMatrices = worldMatrixBatches[batchIndex];
                    var properties = colorPropertyBlocks.Length == 0 ? null : colorPropertyBlocks[batchIndex];
                    Graphics.DrawMeshInstanced(
                        mesh,
                        submeshIndex,
                        materials[submeshIndex],
                        worldMatrices,
                        worldMatrices.Length,
                        properties,
                        ShadowCastingMode.On,
                        true,
                        gameObject.layer,
                        null,
                        LightProbeUsage.Off,
                        null);
                }
            }
        }

        private bool CanDraw()
        {
            return enabled &&
                   gameObject.activeInHierarchy &&
                   mesh != null &&
                   materials != null &&
                   materials.Length > 0 &&
                   InstanceCount > 0 &&
                   platformSupportsInstancing;
        }

        private void CheckPlatformSupport()
        {
            platformSupportChecked = true;
            platformSupportsInstancing = SystemInfo.supportsInstancing;
            if (platformSupportsInstancing || capabilityErrorLogged) return;

            capabilityErrorLogged = true;
            Debug.LogError(
                $"ThreeUnity instanced renderer on '{name}' cannot draw because this platform does not support GPU instancing. " +
                "Re-export the source with instancedMeshMode: \"expanded\".",
                this);
        }

        private void EnsureRuntimeState()
        {
            if (materials == null) materials = Array.Empty<Material>();
            if (localMatrices == null) localMatrices = Array.Empty<Matrix4x4>();
            if (instanceColors == null) instanceColors = Array.Empty<Color>();
            if (!ReferenceEquals(batchedMatrixSource, localMatrices) || !ReferenceEquals(batchedColorSource, instanceColors))
                RebuildBatches();
            else
                RefreshReadOnlyMaterials();
        }

        private void RebuildBatches()
        {
            var batchCount = BatchCount;
            localMatrixBatches = new Matrix4x4[batchCount][];
            worldMatrixBatches = new Matrix4x4[batchCount][];
            var hasColors = instanceColors.Length > 0;
            colorBatches = hasColors ? new Vector4[batchCount][] : Array.Empty<Vector4[]>();
            colorPropertyBlocks = hasColors ? new MaterialPropertyBlock[batchCount] : Array.Empty<MaterialPropertyBlock>();

            for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                var sourceOffset = batchIndex * MaxBatchSize;
                var count = Math.Min(MaxBatchSize, InstanceCount - sourceOffset);
                var localBatch = new Matrix4x4[count];
                var worldBatch = new Matrix4x4[count];
                Array.Copy(localMatrices, sourceOffset, localBatch, 0, count);
                localMatrixBatches[batchIndex] = localBatch;
                worldMatrixBatches[batchIndex] = worldBatch;

                if (!hasColors) continue;
                var colorBatch = new Vector4[count];
                for (var index = 0; index < count; index++) colorBatch[index] = instanceColors[sourceOffset + index];
                var block = new MaterialPropertyBlock();
                block.SetVectorArray(InstanceColorId, colorBatch);
                colorBatches[batchIndex] = colorBatch;
                colorPropertyBlocks[batchIndex] = block;
            }

            batchedMatrixSource = localMatrices;
            batchedColorSource = instanceColors;
            hasCachedLocalToWorld = false;
            RefreshReadOnlyMaterials();
        }

        private void RefreshReadOnlyMaterials()
        {
            if (ReferenceEquals(readOnlyMaterialSource, materials)) return;
            readOnlyMaterialSource = materials;
            readOnlyMaterials = Array.AsReadOnly(materials ?? Array.Empty<Material>());
        }

        private void UpdateWorldMatrices()
        {
            var localToWorld = transform.localToWorldMatrix;
            if (hasCachedLocalToWorld && MatricesEqual(localToWorld, cachedLocalToWorld)) return;

            for (var batchIndex = 0; batchIndex < localMatrixBatches.Length; batchIndex++)
            {
                var localBatch = localMatrixBatches[batchIndex];
                var worldBatch = worldMatrixBatches[batchIndex];
                for (var index = 0; index < localBatch.Length; index++)
                    worldBatch[index] = localToWorld * localBatch[index];
            }
            cachedLocalToWorld = localToWorld;
            hasCachedLocalToWorld = true;
        }

        private void ValidateInstanceIndex(int index)
        {
            if (index < 0 || index >= InstanceCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    $"Instance index {index} is outside the valid range [0, {InstanceCount}).");
            }
        }

        private static bool MatricesEqual(Matrix4x4 left, Matrix4x4 right)
        {
            for (var index = 0; index < 16; index++)
            {
                if (left[index] != right[index]) return false;
            }
            return true;
        }
    }
}

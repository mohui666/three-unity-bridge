using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityInstancedImporterTests
    {
        private const string SamplePath = "Packages/com.three-unity.bridge/Samples~/GPU Instanced Mesh/instanced-mesh-gpu.threeunity";
        private const string ImportedAssetPath = "Assets/InstancedMeshImporterSmoke.threeunity";
        private const string NoColorAssetPath = "Assets/InstancedMeshNoColorSmoke.threeunity";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(ImportedAssetPath);
            AssetDatabase.DeleteAsset(NoColorAssetPath);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(ImportedAssetPath);
            AssetDatabase.DeleteAsset(NoColorAssetPath);
        }

        [Test]
        public void GpuInstancedSampleImportsCompactBatchesMatricesColorsMaterialsAndTransformAnimation()
        {
            var sourceDocument = JsonUtility.FromJson<SampleDocument>(File.ReadAllText(Path.GetFullPath(SamplePath)));
            var sourceRecord = sourceDocument.instancedMeshes.Single(record => record.count >= 2500);
            var sourceNode = sourceDocument.nodes.Single(node => node.instancedMeshId == sourceRecord.id);
            var sourceMesh = sourceDocument.meshes.Single(mesh => mesh.id == sourceRecord.meshId);

            File.Copy(Path.GetFullPath(SamplePath), Path.GetFullPath(ImportedAssetPath));
            AssetDatabase.ImportAsset(ImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(ImportedAssetPath);
            Assert.That(root, Is.Not.Null, "The GPU Instanced Mesh sample must import as a GameObject asset.");
            var metadata = root.GetComponentsInChildren<ThreeUnityMetadata>(true)
                .Single(candidate => candidate.SourceNodeId == sourceNode.id);
            var renderer = metadata.GetComponent<ThreeUnityInstancedRenderer>();
            Assert.That(renderer, Is.Not.Null);
            Assert.That(metadata.GetComponent<MeshFilter>(), Is.Null, "A native instanced node must not add a MeshFilter.");
            Assert.That(metadata.GetComponent<MeshRenderer>(), Is.Null, "A native instanced node must not add a duplicate MeshRenderer.");
            Assert.That(metadata.GetComponent<SkinnedMeshRenderer>(), Is.Null, "A native instanced node must not add a SkinnedMeshRenderer.");
            Assert.That(root.GetComponentsInChildren<Transform>(true).Length, Is.LessThan(sourceRecord.count), "Instances must not expand into child GameObjects.");

            Assert.That(renderer.InstanceCount, Is.EqualTo(sourceRecord.count));
            Assert.That(renderer.BatchCount, Is.EqualTo((sourceRecord.count + 1022) / 1023));
            Assert.That(renderer.SharedMesh, Is.Not.Null);
            Assert.That(renderer.SharedMesh.subMeshCount, Is.EqualTo(Math.Max(1, sourceMesh.groups?.Length ?? 0)));
            Assert.That(renderer.SharedMaterials, Has.Count.EqualTo(renderer.SharedMesh.subMeshCount));
            Assert.That(renderer.SharedMaterials.All(material => material != null && material.enableInstancing), Is.True);
            Assert.That(renderer.SharedMaterials.All(material => material.shader.name == "ThreeUnity/Instanced Surface"), Is.True);

            var sampledIndices = new[] { 0, Math.Min(1022, sourceRecord.count - 1), sourceRecord.count - 1 }.Distinct();
            foreach (var index in sampledIndices)
            {
                AssertMatrix(renderer.GetLocalMatrix(index), ConvertExpectedMatrix(sourceRecord.matrices, index, sourceDocument.unitScaleMeters), $"matrix {index}");
                AssertColor(renderer.GetInstanceColor(index), sourceRecord.colors, index, $"color {index}");
            }

            var shearIndex = FindShearMatrix(sourceRecord.matrices, sourceRecord.count);
            Assert.That(shearIndex, Is.GreaterThanOrEqualTo(0), "The sample must contain a full affine matrix with shear.");
            var importedShear = renderer.GetLocalMatrix(shearIndex);
            Assert.That(MaximumBasisDot(importedShear), Is.GreaterThan(0.0001f), "Matrix conversion must preserve shear instead of decomposing to TRS.");

            var clips = AssetDatabase.LoadAllAssetsAtPath(ImportedAssetPath).OfType<AnimationClip>().ToArray();
            Assert.That(clips, Is.Not.Empty);
            var nodePath = AnimationUtility.CalculateTransformPath(metadata.transform, root.transform);
            Assert.That(clips.SelectMany(AnimationUtility.GetCurveBindings).Any(binding =>
                binding.type == typeof(Transform) &&
                binding.path == nodePath &&
                binding.propertyName.StartsWith("m_LocalRotation.")), Is.True, "Transform animation must continue targeting the compact instanced node.");

            var noColorJson = RemoveInstancedColors(File.ReadAllText(Path.GetFullPath(SamplePath)))
                .Replace("\"vertexColors\": false", "\"vertexColors\": true");
            File.WriteAllText(Path.GetFullPath(NoColorAssetPath), noColorJson);
            AssetDatabase.ImportAsset(NoColorAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var noColorRoot = AssetDatabase.LoadAssetAtPath<GameObject>(NoColorAssetPath);
            var noColorRenderer = noColorRoot.GetComponentInChildren<ThreeUnityInstancedRenderer>(true);
            Assert.That(noColorRenderer, Is.Not.Null);
            Assert.That(noColorRenderer.GetInstanceColor(0), Is.EqualTo(Color.white), "Missing instance colors must stay implicit white.");
            Assert.That(noColorRenderer.SharedMaterials.All(material =>
                material.enableInstancing && material.shader.name == "ThreeUnity/Vertex Color"), Is.True,
                "The no-color path must reuse instancing-capable base materials instead of creating colored variants.");
            Assert.That(
                AssetDatabase.LoadAllAssetsAtPath(NoColorAssetPath).OfType<Material>().Count(),
                Is.EqualTo(noColorRenderer.SharedMaterials.Distinct().Count()),
                "The no-color import must not create additional instanced-color material variants.");

            Debug.Log($"THREE_UNITY_INSTANCED_IMPORT_PASS instances={renderer.InstanceCount} batches={renderer.BatchCount} submeshes={renderer.SharedMesh.subMeshCount} hierarchy={root.GetComponentsInChildren<Transform>(true).Length} shearIndex={shearIndex}");
        }

        private static string RemoveInstancedColors(string json)
        {
            var recordsOffset = json.IndexOf("\"instancedMeshes\"", StringComparison.Ordinal);
            var colorsOffset = json.IndexOf("\"colors\": [", recordsOffset, StringComparison.Ordinal);
            var valuesOffset = colorsOffset + "\"colors\": [".Length;
            var valuesEnd = json.IndexOf(']', valuesOffset);
            return json.Substring(0, valuesOffset) + json.Substring(valuesEnd);
        }

        private static Matrix4x4 ConvertExpectedMatrix(float[] values, int index, float unitScale)
        {
            var source = new Matrix4x4();
            var offset = index * 16;
            for (var column = 0; column < 4; column++)
            for (var row = 0; row < 4; row++)
                source[row, column] = values[offset + column * 4 + row];
            var mirror = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            var converted = mirror * source * mirror;
            converted.m03 *= unitScale;
            converted.m13 *= unitScale;
            converted.m23 *= unitScale;
            return converted;
        }

        private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected, string message)
        {
            for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                Assert.That(actual[row, column], Is.EqualTo(expected[row, column]).Within(0.00001f), $"{message}, row {row}, column {column}");
        }

        private static void AssertColor(Color actual, float[] values, int index, string message)
        {
            var offset = index * 4;
            Assert.That(actual.r, Is.EqualTo(values[offset]).Within(0.00001f), message + " red");
            Assert.That(actual.g, Is.EqualTo(values[offset + 1]).Within(0.00001f), message + " green");
            Assert.That(actual.b, Is.EqualTo(values[offset + 2]).Within(0.00001f), message + " blue");
            Assert.That(actual.a, Is.EqualTo(values[offset + 3]).Within(0.00001f), message + " alpha");
        }

        private static int FindShearMatrix(float[] values, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var matrix = ConvertExpectedMatrix(values, index, 1f);
                if (MaximumBasisDot(matrix) > 0.0001f) return index;
            }
            return -1;
        }

        private static float MaximumBasisDot(Matrix4x4 matrix)
        {
            var x = new Vector3(matrix.m00, matrix.m10, matrix.m20).normalized;
            var y = new Vector3(matrix.m01, matrix.m11, matrix.m21).normalized;
            var z = new Vector3(matrix.m02, matrix.m12, matrix.m22).normalized;
            return Mathf.Max(Mathf.Abs(Vector3.Dot(x, y)), Mathf.Abs(Vector3.Dot(x, z)), Mathf.Abs(Vector3.Dot(y, z)));
        }

        [Serializable]
        private sealed class SampleDocument
        {
            public float unitScaleMeters = 1f;
            public SampleNode[] nodes = Array.Empty<SampleNode>();
            public SampleMesh[] meshes = Array.Empty<SampleMesh>();
            public SampleInstancedMesh[] instancedMeshes = Array.Empty<SampleInstancedMesh>();
        }

        [Serializable]
        private sealed class SampleNode
        {
            public string id;
            public string instancedMeshId;
        }

        [Serializable]
        private sealed class SampleMesh
        {
            public string id;
            public SampleMeshGroup[] groups;
        }

        [Serializable]
        private sealed class SampleMeshGroup
        {
            public int materialIndex;
        }

        [Serializable]
        private sealed class SampleInstancedMesh
        {
            public string id;
            public string meshId;
            public int count;
            public float[] matrices;
            public float[] colors;
        }
    }
}

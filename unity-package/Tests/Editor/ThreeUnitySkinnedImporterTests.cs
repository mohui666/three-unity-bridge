using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnitySkinnedImporterTests
    {
        private const string SamplePath = "Packages/com.three-unity.bridge/Samples~/Animated Skinned Mesh/animated-skinned-mesh.threeunity";
        private const string ImportedAssetPath = "Assets/AnimatedSkinnedMeshImporterSmoke.threeunity";
        private const string VersionOneSamplePath = "Packages/com.three-unity.bridge/Samples~/Imported Triangle/triangle.threeunity";
        private const string VersionOneImportedAssetPath = "Assets/VersionOneImporterSmoke.threeunity";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(ImportedAssetPath);
            AssetDatabase.DeleteAsset(VersionOneImportedAssetPath);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(ImportedAssetPath);
            AssetDatabase.DeleteAsset(VersionOneImportedAssetPath);
        }

        [Test]
        public void AnimatedSampleImportsSkinnedMeshClipAndVisibleDeformation()
        {
            File.Copy(Path.GetFullPath(SamplePath), Path.GetFullPath(ImportedAssetPath));
            AssetDatabase.ImportAsset(ImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(ImportedAssetPath);
            Assert.That(root, Is.Not.Null, "The animated sample must import as a GameObject asset.");

            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.That(renderer, Is.Not.Null, "The animated sample must create a SkinnedMeshRenderer.");
            Assert.That(renderer.bones, Has.Length.EqualTo(3));
            Assert.That(renderer.sharedMesh, Is.Not.Null);
            Assert.That(renderer.sharedMesh.bindposes, Has.Length.EqualTo(renderer.bones.Length));

            var clips = AssetDatabase.LoadAllAssetsAtPath(ImportedAssetPath).OfType<AnimationClip>().ToArray();
            Assert.That(clips, Has.Length.EqualTo(1));
            Assert.That(clips[0].name, Is.EqualTo("Ribbon Bend"));
            Assert.That(clips[0].length, Is.GreaterThan(0f));

            var player = root.GetComponent<ThreeUnityAnimationPlayer>();
            Assert.That(player, Is.Not.Null, "The imported root must carry the default runtime animation player.");
            Assert.That(player.Clips, Has.Length.EqualTo(1));
            Assert.That(player.DefaultClipName, Is.EqualTo("Ribbon Bend"));
            Assert.That(player.PlayOnAwake, Is.True);
            Assert.That(player.Loop, Is.True);

            var instance = UnityEngine.Object.Instantiate(root);
            var startMesh = new Mesh();
            var middleMesh = new Mesh();
            try
            {
                var instanceRenderer = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                clips[0].SampleAnimation(instance, 0f);
                instanceRenderer.BakeMesh(startMesh);
                clips[0].SampleAnimation(instance, clips[0].length * 0.5f);
                instanceRenderer.BakeMesh(middleMesh);

                var startVertices = startMesh.vertices;
                var middleVertices = middleMesh.vertices;
                Assert.That(middleVertices, Has.Length.EqualTo(startVertices.Length));
                var sourceVertices = instanceRenderer.sharedMesh.vertices;
                var maximumRestDelta = sourceVertices.Select((vertex, index) => Vector3.Distance(vertex, startVertices[index])).Max();
                Assert.That(maximumRestDelta, Is.LessThan(0.001f), "The imported bind pose must preserve the authored rest mesh.");
                var maximumVertexDelta = startVertices.Select((vertex, index) => Vector3.Distance(vertex, middleVertices[index])).Max();
                Assert.That(maximumVertexDelta, Is.GreaterThan(0.001f), "Sampling the imported clip must visibly deform at least one skinned vertex.");
                var animatedBounds = instanceRenderer.localBounds;
                var middleBounds = middleMesh.bounds;
                Assert.That(animatedBounds.min.x, Is.LessThanOrEqualTo(middleBounds.min.x));
                Assert.That(animatedBounds.min.y, Is.LessThanOrEqualTo(middleBounds.min.y));
                Assert.That(animatedBounds.min.z, Is.LessThanOrEqualTo(middleBounds.min.z));
                Assert.That(animatedBounds.max.x, Is.GreaterThanOrEqualTo(middleBounds.max.x));
                Assert.That(animatedBounds.max.y, Is.GreaterThanOrEqualTo(middleBounds.max.y));
                Assert.That(animatedBounds.max.z, Is.GreaterThanOrEqualTo(middleBounds.max.z));
                Debug.Log($"THREE_UNITY_SKINNED_IMPORT_PASS bones={renderer.bones.Length} bindposes={renderer.sharedMesh.bindposes.Length} clip={clips[0].name} maxRestDelta={maximumRestDelta:F6} maxVertexDelta={maximumVertexDelta:F6}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(startMesh);
                UnityEngine.Object.DestroyImmediate(middleMesh);
                UnityEngine.Object.DestroyImmediate(instance);
            }

            File.Copy(Path.GetFullPath(VersionOneSamplePath), Path.GetFullPath(VersionOneImportedAssetPath));
            AssetDatabase.ImportAsset(VersionOneImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var versionOneRoot = AssetDatabase.LoadAssetAtPath<GameObject>(VersionOneImportedAssetPath);
            Assert.That(versionOneRoot, Is.Not.Null);
            Assert.That(versionOneRoot.GetComponentInChildren<MeshFilter>(true)?.sharedMesh, Is.Not.Null, "The version 1 triangle must continue importing as a static mesh.");
        }
    }
}

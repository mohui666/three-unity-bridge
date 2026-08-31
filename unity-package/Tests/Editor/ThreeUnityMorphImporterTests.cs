using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityMorphImporterTests
    {
        private const string SamplePath = "Packages/com.three-unity.bridge/Samples~/Morph Target Animation/morph-target-animation.threeunity";
        private const string ImportedAssetPath = "Assets/MorphTargetAnimationImporterSmoke.threeunity";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(ImportedAssetPath);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(ImportedAssetPath);
        }

        [Test]
        public void MorphSampleImportsBlendShapesClipAndVisibleDeformation()
        {
            File.Copy(Path.GetFullPath(SamplePath), Path.GetFullPath(ImportedAssetPath));
            AssetDatabase.ImportAsset(ImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(ImportedAssetPath);
            Assert.That(root, Is.Not.Null, "The morph sample must import as a GameObject asset.");

            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.That(renderer, Is.Not.Null, "A morph-only mesh must use a SkinnedMeshRenderer.");
            Assert.That(renderer.bones, Is.Empty, "A morph-only mesh must not create an empty skeleton.");
            Assert.That(renderer.sharedMesh, Is.Not.Null);
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(2));
            Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("Bulge"));
            Assert.That(renderer.sharedMesh.GetBlendShapeName(1), Is.EqualTo("Twist"));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(25f).Within(0.001f));
            Assert.That(renderer.GetBlendShapeWeight(1), Is.EqualTo(0f).Within(0.001f));

            var clips = AssetDatabase.LoadAllAssetsAtPath(ImportedAssetPath).OfType<AnimationClip>().ToArray();
            Assert.That(clips, Has.Length.EqualTo(1));
            var clip = clips[0];
            Assert.That(clip.name, Is.EqualTo("Morph Cycle"));
            Assert.That(clip.length, Is.GreaterThan(0f));

            var blendShapeBindings = AnimationUtility.GetCurveBindings(clip)
                .Where(binding => binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape."))
                .ToArray();
            Assert.That(blendShapeBindings, Is.Not.Empty, "The imported clip must animate a SkinnedMeshRenderer blend shape.");
            Assert.That(blendShapeBindings.All(binding =>
                binding.propertyName == "blendShape.Bulge" || binding.propertyName == "blendShape.Twist"), Is.True);

            var player = root.GetComponent<ThreeUnityAnimationPlayer>();
            Assert.That(player, Is.Not.Null);
            Assert.That(player.DefaultClipName, Is.EqualTo("Morph Cycle"));
            Assert.That(player.Loop, Is.True);

            var instance = UnityEngine.Object.Instantiate(root);
            var startMesh = new Mesh();
            var middleMesh = new Mesh();
            var restoredMesh = new Mesh();
            try
            {
                var instanceRenderer = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                clip.SampleAnimation(instance, 0f);
                var startBulgeWeight = instanceRenderer.GetBlendShapeWeight(0);
                var startTwistWeight = instanceRenderer.GetBlendShapeWeight(1);
                Assert.That(startBulgeWeight, Is.EqualTo(25f).Within(0.001f));
                Assert.That(startTwistWeight, Is.EqualTo(0f).Within(0.001f));
                instanceRenderer.BakeMesh(startMesh);

                clip.SampleAnimation(instance, clip.length * 0.5f);
                instanceRenderer.BakeMesh(middleMesh);

                var startVertices = startMesh.vertices;
                var middleVertices = middleMesh.vertices;
                Assert.That(middleVertices, Has.Length.EqualTo(startVertices.Length));
                var maximumVertexDelta = startVertices
                    .Select((vertex, index) => Vector3.Distance(vertex, middleVertices[index]))
                    .Max();
                Assert.That(maximumVertexDelta, Is.GreaterThan(0.001f), "The middle of the morph clip must visibly deform at least one vertex.");

                AssertBoundsContain(instanceRenderer.localBounds, startMesh.bounds, "start");
                AssertBoundsContain(instanceRenderer.localBounds, middleMesh.bounds, "middle");

                clip.SampleAnimation(instance, 0f);
                Assert.That(instanceRenderer.GetBlendShapeWeight(0), Is.EqualTo(startBulgeWeight).Within(0.001f));
                Assert.That(instanceRenderer.GetBlendShapeWeight(1), Is.EqualTo(startTwistWeight).Within(0.001f));
                instanceRenderer.BakeMesh(restoredMesh);

                var restoredVertices = restoredMesh.vertices;
                var maximumRestoreDelta = startVertices
                    .Select((vertex, index) => Vector3.Distance(vertex, restoredVertices[index]))
                    .Max();
                Assert.That(maximumRestoreDelta, Is.LessThan(0.00001f), "Sampling the clip start again must restore the initial morph geometry.");
                AssertBoundsContain(instanceRenderer.localBounds, restoredMesh.bounds, "restored");

                Debug.Log($"THREE_UNITY_MORPH_IMPORT_PASS blendShapes={instanceRenderer.sharedMesh.blendShapeCount} initialBulge={startBulgeWeight:F3} initialTwist={startTwistWeight:F3} clip={clip.name} maxVertexDelta={maximumVertexDelta:F6} maxRestoreDelta={maximumRestoreDelta:F6}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(startMesh);
                UnityEngine.Object.DestroyImmediate(middleMesh);
                UnityEngine.Object.DestroyImmediate(restoredMesh);
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void AssertBoundsContain(Bounds outer, Bounds inner, string sample)
        {
            Assert.That(outer.min.x, Is.LessThanOrEqualTo(inner.min.x), $"Animated bounds must contain the {sample} minimum X.");
            Assert.That(outer.min.y, Is.LessThanOrEqualTo(inner.min.y), $"Animated bounds must contain the {sample} minimum Y.");
            Assert.That(outer.min.z, Is.LessThanOrEqualTo(inner.min.z), $"Animated bounds must contain the {sample} minimum Z.");
            Assert.That(outer.max.x, Is.GreaterThanOrEqualTo(inner.max.x), $"Animated bounds must contain the {sample} maximum X.");
            Assert.That(outer.max.y, Is.GreaterThanOrEqualTo(inner.max.y), $"Animated bounds must contain the {sample} maximum Y.");
            Assert.That(outer.max.z, Is.GreaterThanOrEqualTo(inner.max.z), $"Animated bounds must contain the {sample} maximum Z.");
        }
    }
}

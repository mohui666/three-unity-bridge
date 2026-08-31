using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityPrimitiveImporterTests
    {
        private const string SamplePath = "Packages/com.three-unity.bridge/Samples~/Line Points Sprite/non-mesh-primitives.threeunity";
        private const string ImportedAssetPath = "Assets/PrimitiveImporterSmoke.threeunity";

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
        public void PrimitiveSampleImportsTopologyBillboardsMaterialSlotsAndAnimationBindings()
        {
            File.Copy(Path.GetFullPath(SamplePath), Path.GetFullPath(ImportedAssetPath));
            AssetDatabase.ImportAsset(ImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(ImportedAssetPath);
            Assert.That(root, Is.Not.Null, "The Line Points Sprite sample must import as a GameObject asset.");

            var continuous = FindNode(root, "Continuous Line");
            var segments = FindNode(root, "Line Segments");
            var loop = FindNode(root, "Line Loop");
            AssertLine(continuous, new[] { 0, 1, 1, 2, 2, 3, 3, 4, 4, 5 });
            AssertLine(segments, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
            AssertLine(loop, new[] { 0, 1, 1, 2, 2, 3, 3, 4, 4, 0 });

            var points = FindNode(root, "Points Cloud");
            var pointsRenderer = points.GetComponent<MeshRenderer>();
            var pointsMesh = points.GetComponent<MeshFilter>()?.sharedMesh;
            Assert.That(points.GetComponents<Renderer>(), Has.Length.EqualTo(1), "A Points object must use one Renderer.");
            Assert.That(pointsRenderer, Is.Not.Null);
            Assert.That(pointsMesh, Is.Not.Null);
            Assert.That(pointsMesh.vertexCount, Is.EqualTo(30 * 4));
            Assert.That(pointsMesh.subMeshCount, Is.EqualTo(2));
            Assert.That(pointsMesh.GetIndices(0), Has.Length.EqualTo(15 * 6));
            Assert.That(pointsMesh.GetIndices(1), Has.Length.EqualTo(15 * 6));
            Assert.That(pointsMesh.normals, Is.Empty);
            Assert.That(pointsRenderer.sharedMaterials, Has.Length.EqualTo(2));
            foreach (var material in pointsRenderer.sharedMaterials)
            {
                Assert.That(material.shader.name, Is.EqualTo("ThreeUnity/Billboard"));
                Assert.That(material.GetFloat("_PointSize"), Is.EqualTo(28f).Within(0.001f));
                Assert.That(material.GetFloat("_SizeAttenuation"), Is.EqualTo(1f).Within(0.001f));
                Assert.That(material.GetFloat("_BillboardMode"), Is.EqualTo(0f).Within(0.001f));
                Assert.That(material.GetTexture("_BaseMap"), Is.Not.Null);
            }
            var pointColors = pointsMesh.colors;
            Assert.That(pointColors, Has.Length.EqualTo(pointsMesh.vertexCount));
            for (var corner = 1; corner < 4; corner++) Assert.That(pointColors[corner], Is.EqualTo(pointColors[0]).Using(ColorComparer));
            Assert.That(pointColors[4], Is.Not.EqualTo(pointColors[0]).Using(ColorComparer), "Adjacent point quads must preserve distinct source vertex colors.");
            var pointCorners = new List<Vector2>();
            pointsMesh.GetUVs(1, pointCorners);
            Assert.That(pointCorners.Take(4).ToArray(), Is.EqualTo(new[]
            {
                new Vector2(-0.5f, -0.5f),
                new Vector2(0.5f, -0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(-0.5f, 0.5f),
            }));

            var centerSprite = FindNode(root, "Center Sprite");
            var pivotSprite = FindNode(root, "Pivot Sprite");
            AssertSprite(centerSprite, new Vector2(-0.5f, -0.5f), new Vector2(0.5f, 0.5f), Mathf.PI / 8f, true);
            AssertSprite(pivotSprite, Vector2.zero, Vector2.one, -Mathf.PI / 5f, false);
            Assert.That(Shader.Find("ThreeUnity/Unlit Line"), Is.Not.Null);
            Assert.That(Shader.Find("ThreeUnity/Billboard"), Is.Not.Null);

            var clips = AssetDatabase.LoadAllAssetsAtPath(ImportedAssetPath).OfType<AnimationClip>().ToArray();
            Assert.That(clips, Has.Length.EqualTo(1));
            var clip = clips[0];
            var curveBindings = AnimationUtility.GetCurveBindings(clip);
            var continuousPath = AnimationUtility.CalculateTransformPath(continuous, root.transform);
            var pointsPath = AnimationUtility.CalculateTransformPath(points, root.transform);
            var centerSpritePath = AnimationUtility.CalculateTransformPath(centerSprite, root.transform);
            Assert.That(curveBindings.Any(binding => binding.type == typeof(Transform) && binding.path == continuousPath && binding.propertyName == "m_LocalRotation.x"), Is.True);
            Assert.That(curveBindings.Any(binding => binding.type == typeof(Transform) && binding.path == pointsPath && binding.propertyName == "m_LocalPosition.y"), Is.True);
            Assert.That(curveBindings.Any(binding => binding.type == typeof(Transform) && binding.path == centerSpritePath && binding.propertyName == "m_LocalScale.x"), Is.True);

            var instance = Object.Instantiate(root);
            try
            {
                var instancePlayer = instance.GetComponent<ThreeUnityAnimationPlayer>();
                Assert.That(instancePlayer, Is.Not.Null);
                var instancePoints = FindNode(instance, "Points Cloud");
                var instanceCenterSprite = FindNode(instance, "Center Sprite");
                var instanceSpriteRenderer = instanceCenterSprite.GetComponent<MeshRenderer>();
                var sharedMaterial = instanceSpriteRenderer.sharedMaterial;
                var sharedColor = sharedMaterial.GetColor("_BaseColor");

                instancePlayer.Sample(clip.name, 0f);
                var startPointPosition = instancePoints.localPosition;
                var startSpriteScale = instanceCenterSprite.localScale;
                var startBlockColor = ReadBlockColor(instanceSpriteRenderer, 0);
                instancePlayer.Sample(clip.name, clip.length * 0.5f);
                var middleBlockColor = ReadBlockColor(instanceSpriteRenderer, 0);

                Assert.That(Mathf.Abs(instancePoints.localPosition.y - startPointPosition.y), Is.GreaterThan(0.0001f), "Primitive Transform animation must move the point cloud.");
                Assert.That(Vector3.Distance(instanceCenterSprite.localScale, startSpriteScale), Is.GreaterThan(0.0001f), "Primitive Transform animation must scale the sprite.");
                Assert.That(Approximately(middleBlockColor, startBlockColor), Is.False, "Primitive material animation must write the sprite renderer slot.");
                Assert.That(middleBlockColor.a, Is.EqualTo(0.45f).Within(0.01f));
                Assert.That(instanceSpriteRenderer.sharedMaterial, Is.SameAs(sharedMaterial));
                Assert.That(sharedMaterial.GetColor("_BaseColor"), Is.EqualTo(sharedColor).Using(ColorComparer), "Sampling must not mutate the shared primitive material.");

                Debug.Log($"THREE_UNITY_PRIMITIVE_IMPORT_PASS line={continuous.GetComponent<MeshFilter>().sharedMesh.GetIndexCount(0)} segments={segments.GetComponent<MeshFilter>().sharedMesh.GetIndexCount(0)} loop={loop.GetComponent<MeshFilter>().sharedMesh.GetIndexCount(0)} pointsVertices={pointsMesh.vertexCount} pointsIndices={pointsMesh.GetIndexCount(0) + pointsMesh.GetIndexCount(1)} spriteVertices={centerSprite.GetComponent<MeshFilter>().sharedMesh.vertexCount} clip={clip.name}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static Transform FindNode(GameObject root, string sourceName)
        {
            return root.GetComponentsInChildren<Transform>(true).Single(transform => transform.name.StartsWith(sourceName + " ["));
        }

        private static void AssertLine(Transform node, int[] expectedIndices)
        {
            var renderer = node.GetComponent<MeshRenderer>();
            var mesh = node.GetComponent<MeshFilter>()?.sharedMesh;
            Assert.That(renderer, Is.Not.Null);
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.subMeshCount, Is.EqualTo(1));
            Assert.That(mesh.GetTopology(0), Is.EqualTo(MeshTopology.Lines));
            Assert.That(mesh.GetIndices(0), Is.EqualTo(expectedIndices));
            Assert.That(mesh.normals, Is.Empty);
            Assert.That(renderer.sharedMaterials, Has.Length.EqualTo(1));
            Assert.That(renderer.sharedMaterial.shader.name, Is.EqualTo("ThreeUnity/Unlit Line"));
        }

        private static void AssertSprite(Transform node, Vector2 expectedMinimum, Vector2 expectedMaximum, float expectedRotation, bool expectedAttenuation)
        {
            var renderer = node.GetComponent<MeshRenderer>();
            var mesh = node.GetComponent<MeshFilter>()?.sharedMesh;
            Assert.That(renderer, Is.Not.Null);
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.vertexCount, Is.EqualTo(4));
            Assert.That(mesh.GetIndices(0), Has.Length.EqualTo(6));
            Assert.That(mesh.bounds.size.z, Is.GreaterThan(0f), "Sprite bounds must cover camera-facing rotation.");
            var positions = mesh.vertices;
            Assert.That(positions.Min(position => position.x), Is.EqualTo(expectedMinimum.x).Within(0.0001f));
            Assert.That(positions.Min(position => position.y), Is.EqualTo(expectedMinimum.y).Within(0.0001f));
            Assert.That(positions.Max(position => position.x), Is.EqualTo(expectedMaximum.x).Within(0.0001f));
            Assert.That(positions.Max(position => position.y), Is.EqualTo(expectedMaximum.y).Within(0.0001f));
            Assert.That(renderer.sharedMaterials, Has.Length.EqualTo(1));
            Assert.That(renderer.sharedMaterial.shader.name, Is.EqualTo("ThreeUnity/Billboard"));
            Assert.That(renderer.sharedMaterial.GetFloat("_BillboardMode"), Is.EqualTo(1f).Within(0.001f));
            Assert.That(renderer.sharedMaterial.GetFloat("_SpriteRotation"), Is.EqualTo(expectedRotation).Within(0.001f));
            Assert.That(renderer.sharedMaterial.GetFloat("_SizeAttenuation"), Is.EqualTo(expectedAttenuation ? 1f : 0f).Within(0.001f));
            Assert.That(renderer.sharedMaterial.GetTexture("_BaseMap"), Is.Not.Null);
        }

        private static Color ReadBlockColor(Renderer renderer, int materialSlot)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, materialSlot);
            return block.GetColor("_BaseColor");
        }

        private static readonly IEqualityComparer<Color> ColorComparer = new ApproximateColorComparer();

        private static bool Approximately(Color left, Color right) =>
            Mathf.Abs(left.r - right.r) < 0.0001f &&
            Mathf.Abs(left.g - right.g) < 0.0001f &&
            Mathf.Abs(left.b - right.b) < 0.0001f &&
            Mathf.Abs(left.a - right.a) < 0.0001f;

        private sealed class ApproximateColorComparer : IEqualityComparer<Color>
        {
            public bool Equals(Color left, Color right) => Approximately(left, right);

            public int GetHashCode(Color value) => 0;
        }
    }
}

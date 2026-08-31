using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityMaterialAnimationImporterTests
    {
        private const string SamplePath = "Packages/com.three-unity.bridge/Samples~/Material UV Animation/material-uv-animation.threeunity";
        private const string ImportedAssetPath = "Assets/MaterialUvAnimationImporterSmoke.threeunity";

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
        public void MaterialSampleImportsStaticUvAndSamplesRendererSlotPropertyBlocks()
        {
            File.Copy(Path.GetFullPath(SamplePath), Path.GetFullPath(ImportedAssetPath));
            AssetDatabase.ImportAsset(ImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(ImportedAssetPath);
            Assert.That(root, Is.Not.Null, "The material animation sample must import as a GameObject asset.");
            var assetRenderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.That(assetRenderers.Length, Is.GreaterThanOrEqualTo(2));
            var sharedAnimatedMaterial = assetRenderers
                .SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null)
                .GroupBy(material => material)
                .First(group => group.Count() >= 2)
                .Key;
            Assert.That(sharedAnimatedMaterial.IsKeywordEnabled("_EMISSION"), Is.True, "An emissive track must enable emission even when the static color is black.");
            Assert.That(sharedAnimatedMaterial.renderQueue, Is.GreaterThanOrEqualTo(3000), "An opacity track below one must import as a transparent surface.");

            var texture = AssetDatabase.LoadAllAssetsAtPath(ImportedAssetPath).OfType<Texture2D>().FirstOrDefault();
            Assert.That(texture, Is.Not.Null);
            Assert.That(texture.wrapModeU, Is.EqualTo(TextureWrapMode.Repeat));
            Assert.That(texture.wrapModeV, Is.EqualTo(TextureWrapMode.Repeat));

            var texturedMaterial = AssetDatabase.LoadAllAssetsAtPath(ImportedAssetPath)
                .OfType<Material>()
                .FirstOrDefault(material => GetBaseTexture(material) != null);
            Assert.That(texturedMaterial, Is.Not.Null);
            Assert.That(GetBaseTextureScale(texturedMaterial), Is.Not.EqualTo(Vector2.one));
            Assert.That(GetBaseTextureOffset(texturedMaterial), Is.Not.EqualTo(Vector2.zero));

            var instance = Object.Instantiate(root);
            try
            {
                var player = instance.GetComponent<ThreeUnityAnimationPlayer>();
                Assert.That(player, Is.Not.Null);
                Assert.That(player.DefaultClipName, Is.Not.Empty);
                var clip = player.Clips.Single(candidate => candidate.name == player.DefaultClipName);
                Assert.That(clip.length, Is.GreaterThan(0f));
                var curveBindings = AnimationUtility.GetCurveBindings(clip);
                Assert.That(curveBindings.Any(binding => binding.type == typeof(ThreeUnityAnimationPlayer) && binding.propertyName == "materialAnimationClock"), Is.True, "A material-only clip must carry time on the nonvisual player clock.");
                Assert.That(curveBindings.Any(binding => binding.type == typeof(Transform) && binding.path == string.Empty && binding.propertyName == "m_LocalScale.x"), Is.False, "The material clock must not overwrite root scale.");

                var slots = CaptureSlots(instance);
                var sharedReferences = slots.Select(slot => slot.sharedMaterial).ToArray();
                var sharedInitialValues = slots.Select(slot => ReadSharedMaterial(slot.sharedMaterial)).ToArray();

                player.Sample(player.DefaultClipName, 0f);
                var start = ReadPropertyBlocks(slots);
                player.Sample(player.DefaultClipName, clip.length * 0.5f);
                var middle = ReadPropertyBlocks(slots);
                var lateUpdate = typeof(ThreeUnityAnimationPlayer).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(lateUpdate, Is.Not.Null);
                lateUpdate.Invoke(player, null);
                var afterLateUpdate = ReadPropertyBlocks(slots);
                for (var index = 0; index < slots.Count; index++)
                {
                    Assert.That(afterLateUpdate[index].baseColor, Is.EqualTo(middle[index].baseColor).Using(ColorComparer), "A deterministic Sample must persist through LateUpdate.");
                    Assert.That(afterLateUpdate[index].legacyColor, Is.EqualTo(middle[index].legacyColor).Using(ColorComparer), "A deterministic Sample must persist through LateUpdate.");
                    Assert.That(afterLateUpdate[index].emission, Is.EqualTo(middle[index].emission).Using(ColorComparer), "A deterministic Sample must persist through LateUpdate.");
                    Assert.That(afterLateUpdate[index].smoothness, Is.EqualTo(middle[index].smoothness).Within(0.0001f), "A deterministic Sample must persist through LateUpdate.");
                    Assert.That(afterLateUpdate[index].glossiness, Is.EqualTo(middle[index].glossiness).Within(0.0001f), "A deterministic Sample must persist through LateUpdate.");
                    Assert.That(afterLateUpdate[index].baseMapST, Is.EqualTo(middle[index].baseMapST).Using(VectorComparer), "A deterministic Sample must persist through LateUpdate.");
                    Assert.That(afterLateUpdate[index].mainTexST, Is.EqualTo(middle[index].mainTexST).Using(VectorComparer), "A deterministic Sample must persist through LateUpdate.");
                }

                var baseColorChanged = Enumerable.Range(0, slots.Count).Where(index => !Approximately(start[index].baseColor, middle[index].baseColor)).ToArray();
                var opacityChanged = Enumerable.Range(0, slots.Count).Where(index => !Mathf.Approximately(start[index].baseColor.a, middle[index].baseColor.a)).ToArray();
                var emissionChanged = Enumerable.Range(0, slots.Count).Where(index => !Approximately(start[index].emission, middle[index].emission)).ToArray();
                var smoothnessChanged = Enumerable.Range(0, slots.Count).Where(index => !Mathf.Approximately(start[index].smoothness, middle[index].smoothness)).ToArray();
                var stChanged = Enumerable.Range(0, slots.Count).Where(index => !Approximately(start[index].baseMapST, middle[index].baseMapST)).ToArray();

                Assert.That(baseColorChanged, Is.Not.Empty, "Sampling must change base color.");
                Assert.That(opacityChanged, Is.Not.Empty, "Sampling must change opacity.");
                Assert.That(emissionChanged, Is.Not.Empty, "Sampling must change emission.");
                Assert.That(smoothnessChanged, Is.Not.Empty, "Sampling must change roughness through Unity smoothness.");
                Assert.That(stChanged, Is.Not.Empty, "Sampling must change base-map scale or offset.");
                foreach (var index in baseColorChanged)
                {
                    Assert.That(start[index].legacyColor, Is.EqualTo(start[index].baseColor).Using(ColorComparer), "Built-in and URP base-color aliases must match.");
                    Assert.That(middle[index].legacyColor, Is.EqualTo(middle[index].baseColor).Using(ColorComparer), "Built-in and URP base-color aliases must match.");
                }
                foreach (var index in smoothnessChanged)
                {
                    Assert.That(start[index].glossiness, Is.EqualTo(start[index].smoothness).Within(0.0001f), "Built-in and URP smoothness aliases must match.");
                    Assert.That(middle[index].glossiness, Is.EqualTo(middle[index].smoothness).Within(0.0001f), "Built-in and URP smoothness aliases must match.");
                }
                foreach (var index in stChanged)
                {
                    Assert.That(start[index].mainTexST, Is.EqualTo(start[index].baseMapST).Using(VectorComparer), "Built-in and URP base-map ST aliases must match.");
                    Assert.That(middle[index].mainTexST, Is.EqualTo(middle[index].baseMapST).Using(VectorComparer), "Built-in and URP base-map ST aliases must match.");
                }

                var groupedRenderer = slots.Select(slot => slot.renderer).First(renderer => renderer.sharedMaterials.Length > 1);
                var accentSlot = slots.FindIndex(slot => slot.renderer == groupedRenderer && slot.materialSlot == 0);
                var sharedSlot = slots.FindIndex(slot => slot.renderer == groupedRenderer && slot.materialSlot == 1);
                Assert.That(baseColorChanged.Contains(sharedSlot), Is.True, "Source material index 1 must animate grouped renderer slot 1.");
                Assert.That(emissionChanged.Contains(sharedSlot), Is.True);
                Assert.That(smoothnessChanged.Contains(sharedSlot), Is.True);
                Assert.That(stChanged.Contains(sharedSlot), Is.True);
                Assert.That(baseColorChanged.Contains(accentSlot), Is.False, "The neighboring source material index 0 slot must stay static.");
                Assert.That(emissionChanged.Contains(accentSlot), Is.False);
                Assert.That(smoothnessChanged.Contains(accentSlot), Is.False);
                Assert.That(stChanged.Contains(accentSlot), Is.False);

                var sharedAnimatedRenderers = baseColorChanged
                    .GroupBy(index => slots[index].sharedMaterial)
                    .Any(group => group.Select(index => slots[index].renderer).Distinct().Count() >= 2);
                Assert.That(sharedAnimatedRenderers, Is.True, "Two renderers sharing one source material must both receive explicit animation bindings.");

                for (var index = 0; index < slots.Count; index++)
                {
                    Assert.That(slots[index].renderer.sharedMaterials[slots[index].materialSlot], Is.SameAs(sharedReferences[index]));
                    AssertSharedMaterial(sharedReferences[index], sharedInitialValues[index], "Sampling must not mutate a shared material subasset.");
                }

                player.Stop();
                var restored = ReadPropertyBlocks(slots);
                for (var index = 0; index < slots.Count; index++)
                {
                    if (baseColorChanged.Contains(index)) Assert.That(restored[index].baseColor, Is.EqualTo(sharedInitialValues[index].baseColor).Using(ColorComparer));
                    if (baseColorChanged.Contains(index)) Assert.That(restored[index].legacyColor, Is.EqualTo(sharedInitialValues[index].legacyColor).Using(ColorComparer));
                    if (emissionChanged.Contains(index)) Assert.That(restored[index].emission, Is.EqualTo(sharedInitialValues[index].emission).Using(ColorComparer));
                    if (smoothnessChanged.Contains(index)) Assert.That(restored[index].smoothness, Is.EqualTo(sharedInitialValues[index].smoothness).Within(0.0001f));
                    if (smoothnessChanged.Contains(index)) Assert.That(restored[index].glossiness, Is.EqualTo(sharedInitialValues[index].glossiness).Within(0.0001f));
                    if (stChanged.Contains(index)) Assert.That(restored[index].baseMapST, Is.EqualTo(sharedInitialValues[index].baseMapST).Using(VectorComparer));
                    if (stChanged.Contains(index)) Assert.That(restored[index].mainTexST, Is.EqualTo(sharedInitialValues[index].mainTexST).Using(VectorComparer));
                }

                Debug.Log($"THREE_UNITY_MATERIAL_IMPORT_PASS renderers={assetRenderers.Length} slots={slots.Count} clip={clip.name} duration={clip.length:F3} baseColor={baseColorChanged.Length} opacity={opacityChanged.Length} emission={emissionChanged.Length} smoothness={smoothnessChanged.Length} st={stChanged.Length}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static List<RendererSlot> CaptureSlots(GameObject root)
        {
            var result = new List<RendererSlot>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (var materialSlot = 0; materialSlot < materials.Length; materialSlot++)
                {
                    if (materials[materialSlot] != null)
                        result.Add(new RendererSlot(renderer, materialSlot, materials[materialSlot]));
                }
            }
            return result;
        }

        private static PropertyValues[] ReadPropertyBlocks(List<RendererSlot> slots)
        {
            var result = new PropertyValues[slots.Count];
            var block = new MaterialPropertyBlock();
            for (var index = 0; index < slots.Count; index++)
            {
                slots[index].renderer.GetPropertyBlock(block, slots[index].materialSlot);
                result[index] = new PropertyValues
                {
                    baseColor = block.GetColor("_BaseColor"),
                    legacyColor = block.GetColor("_Color"),
                    emission = block.GetColor("_EmissionColor"),
                    smoothness = block.GetFloat("_Smoothness"),
                    glossiness = block.GetFloat("_Glossiness"),
                    baseMapST = block.GetVector("_BaseMap_ST"),
                    mainTexST = block.GetVector("_MainTex_ST"),
                };
                block.Clear();
            }
            return result;
        }

        private static PropertyValues ReadSharedMaterial(Material material)
        {
            var baseColor = GetColor(material, "_BaseColor", "_Color", Color.white);
            var smoothness = material.HasProperty("_Smoothness") ? material.GetFloat("_Smoothness") : material.GetFloat("_Glossiness");
            var st = new Vector4(GetBaseTextureScale(material).x, GetBaseTextureScale(material).y, GetBaseTextureOffset(material).x, GetBaseTextureOffset(material).y);
            return new PropertyValues
            {
                baseColor = baseColor,
                legacyColor = baseColor,
                emission = GetColor(material, "_EmissionColor", "_EmissionColor", Color.black),
                smoothness = smoothness,
                glossiness = smoothness,
                baseMapST = st,
                mainTexST = st,
            };
        }

        private static void AssertSharedMaterial(Material material, PropertyValues expected, string message)
        {
            var actual = ReadSharedMaterial(material);
            Assert.That(actual.baseColor, Is.EqualTo(expected.baseColor).Using(ColorComparer), message);
            Assert.That(actual.legacyColor, Is.EqualTo(expected.legacyColor).Using(ColorComparer), message);
            Assert.That(actual.emission, Is.EqualTo(expected.emission).Using(ColorComparer), message);
            Assert.That(actual.smoothness, Is.EqualTo(expected.smoothness).Within(0.0001f), message);
            Assert.That(actual.glossiness, Is.EqualTo(expected.glossiness).Within(0.0001f), message);
            Assert.That(actual.baseMapST, Is.EqualTo(expected.baseMapST).Using(VectorComparer), message);
            Assert.That(actual.mainTexST, Is.EqualTo(expected.mainTexST).Using(VectorComparer), message);
        }

        private static Texture GetBaseTexture(Material material)
        {
            if (material.HasProperty("_BaseMap")) return material.GetTexture("_BaseMap");
            return material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        }

        private static Vector2 GetBaseTextureScale(Material material)
        {
            return material.HasProperty("_BaseMap") ? material.GetTextureScale("_BaseMap") : material.GetTextureScale("_MainTex");
        }

        private static Vector2 GetBaseTextureOffset(Material material)
        {
            return material.HasProperty("_BaseMap") ? material.GetTextureOffset("_BaseMap") : material.GetTextureOffset("_MainTex");
        }

        private static Color GetColor(Material material, string preferred, string fallback, Color defaultValue)
        {
            if (material.HasProperty(preferred)) return material.GetColor(preferred);
            return material.HasProperty(fallback) ? material.GetColor(fallback) : defaultValue;
        }

        private static bool Approximately(Color left, Color right) =>
            Mathf.Abs(left.r - right.r) < 0.0001f &&
            Mathf.Abs(left.g - right.g) < 0.0001f &&
            Mathf.Abs(left.b - right.b) < 0.0001f &&
            Mathf.Abs(left.a - right.a) < 0.0001f;

        private static bool Approximately(Vector4 left, Vector4 right) => (left - right).sqrMagnitude < 0.00000001f;

        private static readonly IEqualityComparer<Color> ColorComparer = new ApproximateColorComparer();
        private static readonly IEqualityComparer<Vector4> VectorComparer = new ApproximateVectorComparer();

        private readonly struct RendererSlot
        {
            public readonly Renderer renderer;
            public readonly int materialSlot;
            public readonly Material sharedMaterial;

            public RendererSlot(Renderer renderer, int materialSlot, Material sharedMaterial)
            {
                this.renderer = renderer;
                this.materialSlot = materialSlot;
                this.sharedMaterial = sharedMaterial;
            }
        }

        private struct PropertyValues
        {
            public Color baseColor;
            public Color legacyColor;
            public Color emission;
            public float smoothness;
            public float glossiness;
            public Vector4 baseMapST;
            public Vector4 mainTexST;
        }

        private sealed class ApproximateColorComparer : IEqualityComparer<Color>
        {
            public bool Equals(Color left, Color right) => Approximately(left, right);
            public int GetHashCode(Color value) => 0;
        }

        private sealed class ApproximateVectorComparer : IEqualityComparer<Vector4>
        {
            public bool Equals(Vector4 left, Vector4 right) => Approximately(left, right);
            public int GetHashCode(Vector4 value) => 0;
        }
    }
}

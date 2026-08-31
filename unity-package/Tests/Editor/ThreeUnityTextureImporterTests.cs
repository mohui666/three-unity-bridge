using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityTextureImporterTests
    {
        private const string SamplePath = "Packages/com.three-unity.bridge/Samples~/Texture Sources and DataTexture/texture-pipeline-v7.threeunity";
        private const string ImportedAssetPath = "Assets/TexturePipelineImporterSmoke.threeunity";
        private const string SharedTextureId = "texture_70000000000040008000000000000001";

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
        public void TexturePipelineImportsPixelsFormatsSamplerColorSpaceAndSharedMaterialReferences()
        {
            var sourceJson = File.ReadAllText(Path.GetFullPath(SamplePath));
            var integrationJson = sourceJson
                .Replace("\"unlit\": true", "\"unlit\": false")
                .Replace("\"emissiveTextureId\": \"\"", $"\"emissiveTextureId\": \"{SharedTextureId}\"")
                .Replace("\"normalTextureId\": \"\"", $"\"normalTextureId\": \"{SharedTextureId}\"");
            integrationJson = SetMaterialEmissive(integrationJson, "Local Asymmetric PNG Panel Material", "0.25, 0.125, 0.0625");
            Assert.That(integrationJson, Is.Not.EqualTo(sourceJson), "The sample must expose the expected material fields for integration assertions.");

            File.WriteAllText(Path.GetFullPath(ImportedAssetPath), integrationJson);
            AssetDatabase.ImportAsset(ImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(ImportedAssetPath);
            Assert.That(root, Is.Not.Null, "The version 7 texture sample must import as a GameObject asset.");
            var assets = AssetDatabase.LoadAllAssetsAtPath(ImportedAssetPath);
            var textures = assets.OfType<Texture2D>().ToDictionary(texture => texture.name);
            Assert.That(textures, Has.Count.EqualTo(7));

            var png = textures["Local Asymmetric PNG"];
            var jpeg = textures["Loopback Asymmetric JPEG"];
            var r8 = textures["Uint8 R"];
            var rg8 = textures["Uint8 RG"];
            var rgba8 = textures["Uint8 RGBA"];
            var rgbHalf = textures["HalfFloat RGB"];
            var rgbaFloat = textures["Float32 RGBA"];

            Assert.That((png.width, png.height), Is.EqualTo((8, 8)));
            Assert.That((jpeg.width, jpeg.height), Is.EqualTo((8, 8)));
            Assert.That(IsSrgb(png), Is.True);
            Assert.That(IsSrgb(jpeg), Is.True);
            Assert.That(IsSrgb(r8), Is.False);
            Assert.That(IsSrgb(rg8), Is.False);
            Assert.That(IsSrgb(rgba8), Is.False);
            Assert.That(IsSrgb(rgbHalf), Is.False);
            Assert.That(IsSrgb(rgbaFloat), Is.False);

            AssertEncodedPixel(png, 0, 0, new Color32(0, 0, 255, 255), 0, "PNG bottom-left");
            AssertEncodedPixel(png, 7, 0, new Color32(255, 255, 0, 255), 0, "PNG bottom-right");
            AssertEncodedPixel(png, 0, 7, new Color32(255, 0, 0, 255), 0, "PNG top-left");
            AssertEncodedPixel(png, 7, 7, new Color32(0, 255, 0, 255), 0, "PNG top-right");
            AssertEncodedPixel(jpeg, 0, 0, new Color32(0, 0, 255, 255), 12, "JPEG bottom-left");
            AssertEncodedPixel(jpeg, 7, 0, new Color32(255, 255, 0, 255), 12, "JPEG bottom-right");
            AssertEncodedPixel(jpeg, 0, 7, new Color32(255, 0, 0, 255), 12, "JPEG top-left");
            AssertEncodedPixel(jpeg, 7, 7, new Color32(0, 255, 0, 255), 12, "JPEG top-right");

            Assert.That(r8.format, Is.EqualTo(TextureFormat.R8));
            Assert.That(rg8.format, Is.EqualTo(TextureFormat.RG16));
            Assert.That(rgba8.format, Is.EqualTo(TextureFormat.RGBA32));
            Assert.That(rgbHalf.format, Is.EqualTo(TextureFormat.RGBAHalf));
            Assert.That(rgbaFloat.format, Is.EqualTo(TextureFormat.RGBAFloat));

            var r8Bytes = r8.GetRawTextureData<byte>();
            Assert.That(r8Bytes.Take(4).ToArray(), Is.EqualTo(new byte[] { 0, 85, 170, 255 }));
            var rg8Bytes = rg8.GetRawTextureData<byte>();
            Assert.That(rg8Bytes[0], Is.EqualTo(0));
            Assert.That(rg8Bytes[1], Is.EqualTo(0));
            Assert.That(rg8Bytes[(3 * 4 + 3) * 2], Is.EqualTo(255));
            Assert.That(rg8Bytes[(3 * 4 + 3) * 2 + 1], Is.EqualTo(255));
            var rgba8Bytes = rgba8.GetRawTextureData<byte>();
            AssertRgba8(rgba8Bytes, 0, 255, 48, 32, 255, "RGBA8 bottom-left must retain raw row zero when flipY=false.");
            AssertRgba8(rgba8Bytes, (3 * 4 + 0) * 4, 48, 96, 255, 255, "RGBA8 top-left must retain the final raw row.");

            var halfValues = rgbHalf.GetRawTextureData<ushort>();
            Assert.That(halfValues[0], Is.EqualTo(0x3c00), "RGB half red must remain 1.0.");
            Assert.That(halfValues[1], Is.EqualTo(0x0000));
            Assert.That(halfValues[2], Is.EqualTo(0x0000));
            Assert.That(halfValues[3], Is.EqualTo(0x3c00), "Expanded RGB half alpha must be exactly 1.0.");
            var halfBlueOffset = (3 * 4 + 0) * 4;
            Assert.That(halfValues[halfBlueOffset], Is.EqualTo(0x0000));
            Assert.That(halfValues[halfBlueOffset + 1], Is.EqualTo(0x0000));
            Assert.That(halfValues[halfBlueOffset + 2], Is.EqualTo(0x3c00));
            Assert.That(halfValues[halfBlueOffset + 3], Is.EqualTo(0x3c00));

            var floatValues = rgbaFloat.GetRawTextureData<float>();
            AssertFloatPixel(floatValues, 0, 0f, 0f, 0f, 1f, "Float32 bottom-left");
            AssertFloatPixel(floatValues, (3 * 4 + 3) * 4, 1f, 1f, 0f, 1f, "Float32 top-right");

            AssertSampler(png, TextureWrapMode.Repeat, TextureWrapMode.Clamp, FilterMode.Trilinear, 4, 4);
            AssertSampler(jpeg, TextureWrapMode.Mirror, TextureWrapMode.Repeat, FilterMode.Bilinear, 1, 2);
            AssertSampler(r8, TextureWrapMode.Clamp, TextureWrapMode.Clamp, FilterMode.Point, 1, 1);
            AssertSampler(rg8, TextureWrapMode.Repeat, TextureWrapMode.Mirror, FilterMode.Bilinear, 1, 2);
            AssertSampler(rgba8, TextureWrapMode.Mirror, TextureWrapMode.Clamp, FilterMode.Point, 3, 3);
            AssertSampler(rgbHalf, TextureWrapMode.Repeat, TextureWrapMode.Repeat, FilterMode.Trilinear, 3, 8);
            AssertSampler(rgbaFloat, TextureWrapMode.Clamp, TextureWrapMode.Mirror, FilterMode.Bilinear, 1, 4);

            var materials = assets.OfType<Material>().ToDictionary(material => material.name);
            var localMaterial = materials["Local Asymmetric PNG Panel Material"];
            var sharedMaterial = materials["Local PNG Shared Reference Panel Material"];
            Assert.That(GetBaseTexture(localMaterial), Is.SameAs(png));
            Assert.That(GetBaseTexture(sharedMaterial), Is.SameAs(png), "Two source materials must share one imported Texture2D subasset.");
            Assert.That(GetBaseTextureScale(localMaterial), Is.EqualTo(new Vector2(1.75f, 1.25f)).Using(Vector2Comparer));
            Assert.That(GetBaseTextureOffset(localMaterial), Is.EqualTo(new Vector2(0.08f, 0.05f)).Using(Vector2Comparer));
            Assert.That(localMaterial.IsKeywordEnabled("_NORMALMAP"), Is.True);
            Assert.That(localMaterial.GetTexture("_BumpMap"), Is.SameAs(png));
            Assert.That(localMaterial.GetTexture("_EmissionMap"), Is.SameAs(png));
            Assert.That(
                localMaterial.IsKeywordEnabled("_EMISSION"),
                Is.True,
                $"shader={localMaterial.shader.name} keywords={string.Join(",", localMaterial.shaderKeywords)} emission={localMaterial.GetColor("_EmissionColor")} map={localMaterial.GetTexture("_EmissionMap")?.name}");

            Debug.Log($"THREE_UNITY_TEXTURE_IMPORT_PASS textures={textures.Count} materials={materials.Count} png={png.width}x{png.height} half={rgbHalf.format} float={rgbaFloat.format} shared={ReferenceEquals(GetBaseTexture(localMaterial), GetBaseTexture(sharedMaterial))}");
        }

        private static bool IsSrgb(Texture2D texture) => texture.isDataSRGB;

        private static void AssertEncodedPixel(Texture2D texture, int x, int y, Color32 expected, int tolerance, string message)
        {
            var actual = texture.GetPixels32(0)[y * texture.width + x];
            Assert.That(Math.Abs(actual.r - expected.r), Is.LessThanOrEqualTo(tolerance), message + " red");
            Assert.That(Math.Abs(actual.g - expected.g), Is.LessThanOrEqualTo(tolerance), message + " green");
            Assert.That(Math.Abs(actual.b - expected.b), Is.LessThanOrEqualTo(tolerance), message + " blue");
            Assert.That(Math.Abs(actual.a - expected.a), Is.LessThanOrEqualTo(tolerance), message + " alpha");
        }

        private static void AssertRgba8(Unity.Collections.NativeArray<byte> values, int offset, byte r, byte g, byte b, byte a, string message)
        {
            Assert.That(values[offset], Is.EqualTo(r), message + " red");
            Assert.That(values[offset + 1], Is.EqualTo(g), message + " green");
            Assert.That(values[offset + 2], Is.EqualTo(b), message + " blue");
            Assert.That(values[offset + 3], Is.EqualTo(a), message + " alpha");
        }

        private static void AssertFloatPixel(Unity.Collections.NativeArray<float> values, int offset, float r, float g, float b, float a, string message)
        {
            Assert.That(values[offset], Is.EqualTo(r).Within(0.00001f), message + " red");
            Assert.That(values[offset + 1], Is.EqualTo(g).Within(0.00001f), message + " green");
            Assert.That(values[offset + 2], Is.EqualTo(b).Within(0.00001f), message + " blue");
            Assert.That(values[offset + 3], Is.EqualTo(a).Within(0.00001f), message + " alpha");
        }

        private static void AssertSampler(Texture2D texture, TextureWrapMode wrapU, TextureWrapMode wrapV, FilterMode filter, int mipmapCount, int anisotropy)
        {
            Assert.That(texture.wrapModeU, Is.EqualTo(wrapU), texture.name + " wrap U");
            Assert.That(texture.wrapModeV, Is.EqualTo(wrapV), texture.name + " wrap V");
            Assert.That(texture.filterMode, Is.EqualTo(filter), texture.name + " filter");
            Assert.That(texture.mipmapCount, Is.EqualTo(mipmapCount), texture.name + " mipmaps");
            Assert.That(texture.anisoLevel, Is.EqualTo(anisotropy), texture.name + " anisotropy");
        }

        private static Texture GetBaseTexture(Material material)
        {
            if (material.HasProperty("_BaseMap")) return material.GetTexture("_BaseMap");
            return material.GetTexture("_MainTex");
        }

        private static Vector2 GetBaseTextureScale(Material material)
        {
            return material.HasProperty("_BaseMap") ? material.GetTextureScale("_BaseMap") : material.GetTextureScale("_MainTex");
        }

        private static Vector2 GetBaseTextureOffset(Material material)
        {
            return material.HasProperty("_BaseMap") ? material.GetTextureOffset("_BaseMap") : material.GetTextureOffset("_MainTex");
        }

        private static string SetMaterialEmissive(string json, string materialName, string values)
        {
            var materialOffset = json.IndexOf($"\"name\": \"{materialName}\"", StringComparison.Ordinal);
            Assert.That(materialOffset, Is.GreaterThanOrEqualTo(0));
            var propertyOffset = json.IndexOf("\"emissive\":", materialOffset, StringComparison.Ordinal);
            var openingBracket = json.IndexOf('[', propertyOffset);
            var closingBracket = json.IndexOf(']', openingBracket);
            Assert.That(propertyOffset, Is.GreaterThanOrEqualTo(0));
            Assert.That(openingBracket, Is.GreaterThan(propertyOffset));
            Assert.That(closingBracket, Is.GreaterThan(openingBracket));
            return json.Substring(0, openingBracket + 1) + values + json.Substring(closingBracket);
        }

        private static readonly Vector2EqualityComparer Vector2Comparer = new Vector2EqualityComparer();

        private sealed class Vector2EqualityComparer : System.Collections.Generic.IEqualityComparer<Vector2>
        {
            public bool Equals(Vector2 left, Vector2 right) => (left - right).sqrMagnitude < 0.00000001f;
            public int GetHashCode(Vector2 value) => value.GetHashCode();
        }
    }
}

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityPbrMaterialImporterTests
    {
        private const string SamplePath = "Packages/com.three-unity.bridge/Samples~/PBR Material Maps/pbr-material-maps-v8.threeunity";
        private const string LegacySamplePath = "Packages/com.three-unity.bridge/Samples~/Texture Sources and DataTexture/texture-pipeline-v7.threeunity";
        private const string ImportedSamplePath = "Assets/PbrMaterialMapsImporterSmoke.threeunity";
        private const string FixturePath = "Assets/PbrMaterialMapsImporterFixture.threeunity";
        private const string LegacyPath = "Assets/PbrMaterialMapsLegacyFixture.threeunity";

        [SetUp]
        public void SetUp()
        {
            DeleteImportedAssets();
        }

        [TearDown]
        public void TearDown()
        {
            DeleteImportedAssets();
        }

        [Test]
        public void PbrSampleImportsPackedMapsTangentsScaledNormalsEmissionAndInstancedIdentity()
        {
            var sourceJson = File.ReadAllText(Path.GetFullPath(SamplePath));
            var document = JsonUtility.FromJson<Document>(sourceJson);
            Assert.That(document.version, Is.EqualTo(8));

            File.Copy(Path.GetFullPath(SamplePath), Path.GetFullPath(ImportedSamplePath), true);
            Import(ImportedSamplePath);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(ImportedSamplePath);
            Assert.That(root, Is.Not.Null, "The format v8 PBR sample must import as a GameObject asset.");
            var assets = AssetDatabase.LoadAllAssetsAtPath(ImportedSamplePath);
            var textures = assets.OfType<Texture2D>().ToDictionary(texture => texture.name);
            var materials = assets.OfType<Material>().ToDictionary(material => material.name);
            var sourceTextures = document.textures.ToDictionary(record => record.id, record => textures[record.name]);

            var sharedRecord = document.materials.Single(record => record.name == "Shared ORM PBR");
            var separateRecord = document.materials.Single(record => record.name == "Separate PBR Maps");
            var emissionRecord = document.materials.Single(record => record.name == "Normal Emissive Pulse");
            Assert.That(sharedRecord.metalnessTextureId, Is.EqualTo(sharedRecord.roughnessTextureId), "The shared-map sample must exercise one source texture in both PBR roles.");
            Assert.That(separateRecord.metalnessTextureId, Is.Not.EqualTo(separateRecord.roughnessTextureId), "The separate-map sample must exercise compatible independent source textures.");

            var sharedPacked = AssertPackedMaterial(sharedRecord, materials, textures, sourceTextures);
            var separatePacked = AssertPackedMaterial(separateRecord, materials, textures, sourceTextures);
            AssertUniformNormal(sharedRecord, materials[sharedRecord.name], sourceTextures[sharedRecord.normalTextureId]);
            AssertScaledNormal(separateRecord, materials[separateRecord.name], textures, sourceTextures[separateRecord.normalTextureId], false);
            AssertScaledNormal(emissionRecord, materials[emissionRecord.name], textures, sourceTextures[emissionRecord.normalTextureId], true);
            AssertEmission(root, emissionRecord, materials[emissionRecord.name], sourceTextures[emissionRecord.emissiveTextureId]);

            File.WriteAllText(Path.GetFullPath(FixturePath), JsonUtility.ToJson(BuildFixture(), true));
            Import(FixturePath);
            var fixtureRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FixturePath);
            Assert.That(fixtureRoot, Is.Not.Null);
            var fixtureAssets = AssetDatabase.LoadAllAssetsAtPath(FixturePath);
            var fixtureMeshes = fixtureAssets.OfType<Mesh>().ToDictionary(mesh => mesh.name);
            var provided = fixtureMeshes["Provided Tangents"];
            Assert.That(provided.tangents, Has.Length.EqualTo(provided.vertexCount));
            Assert.That(provided.tangents[0], Is.EqualTo(new Vector4(0.6f, 0f, -0.8f, -1f)).Using(Vector4Comparer), "Tangent Z and handedness w must each be mirrored exactly once.");
            var recalculated = fixtureMeshes["Recalculated Tangents"];
            Assert.That(recalculated.tangents, Has.Length.EqualTo(recalculated.vertexCount));
            Assert.That(recalculated.tangents.Any(tangent => new Vector3(tangent.x, tangent.y, tangent.z).sqrMagnitude > 0.9f), Is.True, "A normal-mapped mesh without source tangents must recalculate them when normals and uv0 exist.");

            var fixtureMaterials = fixtureAssets.OfType<Material>().ToArray();
            var fixtureBase = fixtureMaterials.Single(material => material.name == "Fixture PBR");
            var variant = fixtureMaterials.Single(material => material.name == "Fixture PBR Instanced Color");
            var lambertBase = fixtureMaterials.Single(material => material.name == "Fixture Lambert");
            var lambertVariant = fixtureMaterials.Single(material => material.name == "Fixture Lambert Instanced Color");
            var regularRenderer = fixtureRoot.GetComponentsInChildren<MeshRenderer>(true).Single();
            var instancedRenderer = fixtureRoot.GetComponentsInChildren<ThreeUnityInstancedRenderer>(true)
                .Single(renderer => renderer.SharedMaterials.Any(material => material == variant));
            Assert.That(regularRenderer.sharedMaterial, Is.SameAs(fixtureBase), "A renderer must reference the source Material subasset rather than a runtime clone.");
            Assert.That(instancedRenderer, Is.Not.Null);
            Assert.That(instancedRenderer.SharedMaterials, Has.Count.EqualTo(1));
            Assert.That(instancedRenderer.SharedMaterials[0], Is.SameAs(variant));
            Assert.That(variant.GetTexture("_MetallicGlossMap"), Is.SameAs(fixtureBase.GetTexture("_MetallicGlossMap")), "The instanced-color variant must reuse the same derived PBR texture subasset.");
            Assert.That(variant.GetTexture("_BumpMap"), Is.SameAs(fixtureBase.GetTexture("_BumpMap")));
            Assert.That(fixtureBase.GetFloat("_ThreeUnityPbrV8"), Is.EqualTo(1f).Within(0.0001f), "A v8 MeshStandardMaterial using the custom shader must activate the PBR path.");
            Assert.That(variant.GetFloat("_ThreeUnityPbrV8"), Is.EqualTo(1f).Within(0.0001f), "A v8 instanced variant must activate the v8 custom-shader path explicitly.");
            Assert.That(lambertBase.GetFloat("_ThreeUnityPbrV8"), Is.EqualTo(0f).Within(0.0001f), "A v8 MeshLambertMaterial must keep the legacy approximation path.");
            Assert.That(lambertVariant.GetFloat("_ThreeUnityPbrV8"), Is.EqualTo(0f).Within(0.0001f), "A v8 Lambert instanced variant must not infer PBR support from the document version.");

            File.Copy(Path.GetFullPath(LegacySamplePath), Path.GetFullPath(LegacyPath), true);
            Import(LegacyPath);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(LegacyPath), Is.Not.Null, "The existing format v7 texture fixture must continue importing through importer revision 12.");

            Debug.Log($"THREE_UNITY_PBR_IMPORT_PASS shared={sharedPacked.name} separate={separatePacked.name} tangents={provided.tangents.Length}/{recalculated.tangents.Length} instanced={variant.name} legacy=v7");
        }

        private static Texture2D AssertPackedMaterial(
            MaterialRecord record,
            System.Collections.Generic.IReadOnlyDictionary<string, Material> materials,
            System.Collections.Generic.IReadOnlyDictionary<string, Texture2D> textures,
            System.Collections.Generic.IReadOnlyDictionary<string, Texture2D> sourceTextures)
        {
            var material = materials[record.name];
            var derived = textures[record.name + " Metallic Smoothness"];
            Assert.That(derived.isDataSRGB, Is.False, "A derived Metallic/Smoothness map must be linear.");
            var metalness = string.IsNullOrEmpty(record.metalnessTextureId) ? null : sourceTextures[record.metalnessTextureId];
            var roughness = string.IsNullOrEmpty(record.roughnessTextureId) ? null : sourceTextures[record.roughnessTextureId];
            var samplerSource = metalness != null ? metalness : roughness;
            Assert.That((derived.width, derived.height), Is.EqualTo((samplerSource.width, samplerSource.height)));
            Assert.That(derived.wrapModeU, Is.EqualTo(samplerSource.wrapModeU));
            Assert.That(derived.wrapModeV, Is.EqualTo(samplerSource.wrapModeV));
            Assert.That(derived.filterMode, Is.EqualTo(samplerSource.filterMode));
            Assert.That(derived.mipmapCount, Is.EqualTo(samplerSource.mipmapCount));
            Assert.That(derived.anisoLevel, Is.EqualTo(samplerSource.anisoLevel));

            var metalPixels = metalness == null ? null : metalness.GetPixels(0);
            var roughPixels = roughness == null ? null : roughness.GetPixels(0);
            var actual = derived.GetPixels(0);
            var pixel = Enumerable.Range(0, actual.Length).OrderByDescending(index =>
                Math.Abs((metalPixels == null ? 1f : metalPixels[index].b) - 0.5f) +
                Math.Abs((roughPixels == null ? 1f : roughPixels[index].g) - 0.5f)).First();
            var expectedMetallic = Mathf.Clamp01(record.metallic * (metalPixels == null ? 1f : metalPixels[pixel].b));
            var expectedSmoothness = 1f - Mathf.Clamp01(record.roughness * (roughPixels == null ? 1f : roughPixels[pixel].g));
            Assert.That(actual[pixel].r, Is.EqualTo(expectedMetallic).Within(1.5f / 255f), "Packed red must be scalar metallic times source blue.");
            Assert.That(actual[pixel].a, Is.EqualTo(expectedSmoothness).Within(1.5f / 255f), "Packed alpha must be one minus scalar roughness times source green.");
            Assert.That(material.GetTexture("_MetallicGlossMap"), Is.SameAs(derived));
            Assert.That(material.IsKeywordEnabled("_METALLICGLOSSMAP"), Is.True);
            Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(1f).Within(0.0001f));
            if (material.HasProperty("_Smoothness")) Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(1f).Within(0.0001f));
            if (material.HasProperty("_Glossiness")) Assert.That(material.GetFloat("_Glossiness"), Is.EqualTo(1f).Within(0.0001f));
            if (material.HasProperty("_GlossMapScale")) Assert.That(material.GetFloat("_GlossMapScale"), Is.EqualTo(1f).Within(0.0001f));
            if (material.HasProperty("_SmoothnessTextureChannel")) Assert.That(material.GetFloat("_SmoothnessTextureChannel"), Is.EqualTo(0f).Within(0.0001f));
            var st = string.IsNullOrEmpty(record.metalnessTextureId) ? record.roughnessTextureST : record.metalnessTextureST;
            AssertTextureST(material, "_MetallicGlossMap", st);
            return derived;
        }

        private static void AssertUniformNormal(MaterialRecord record, Material material, Texture2D source)
        {
            Assert.That(record.normalScale[0], Is.EqualTo(record.normalScale[1]).Within(0.0001f));
            Assert.That(record.normalScale[0], Is.GreaterThanOrEqualTo(0f));
            Assert.That(material.GetTexture("_BumpMap"), Is.SameAs(source));
            Assert.That(material.GetFloat("_BumpScale"), Is.EqualTo(record.normalScale[0]).Within(0.0001f));
            AssertTextureST(material, "_BumpMap", record.normalTextureST);
        }

        private static void AssertScaledNormal(MaterialRecord record, Material material, System.Collections.Generic.IReadOnlyDictionary<string, Texture2D> textures, Texture2D source, bool expectNegativeY)
        {
            var derived = textures[record.name + " Scaled Normal"];
            Assert.That(derived.isDataSRGB, Is.False);
            Assert.That(material.GetTexture("_BumpMap"), Is.SameAs(derived));
            Assert.That(material.GetFloat("_BumpScale"), Is.EqualTo(1f).Within(0.0001f));
            Assert.That(derived.wrapModeU, Is.EqualTo(source.wrapModeU));
            Assert.That(derived.wrapModeV, Is.EqualTo(source.wrapModeV));
            Assert.That(derived.filterMode, Is.EqualTo(source.filterMode));
            Assert.That(derived.mipmapCount, Is.EqualTo(source.mipmapCount));
            Assert.That(derived.anisoLevel, Is.EqualTo(source.anisoLevel));
            AssertTextureST(material, "_BumpMap", record.normalTextureST);

            var sourcePixels = source.GetPixels(0);
            var pixel = Enumerable.Range(0, sourcePixels.Length)
                .OrderByDescending(index => Math.Abs(sourcePixels[index].g - 0.5f) + Math.Abs(sourcePixels[index].r - 0.5f))
                .First();
            var sourceNormal = DecodeNormal(sourcePixels[pixel]);
            var expected = new Vector3(sourceNormal.x * record.normalScale[0], sourceNormal.y * record.normalScale[1], sourceNormal.z).normalized;
            var actual = DecodeNormal(derived.GetPixels(0)[pixel]).normalized;
            Assert.That(Vector3.Dot(expected, actual), Is.GreaterThan(0.999f), "Scaled Normal RGB must encode the normalized source direction after exact XY scaling.");
            if (expectNegativeY && Math.Abs(sourceNormal.y) > 0.01f)
                Assert.That(Math.Sign(actual.y), Is.EqualTo(Math.Sign(sourceNormal.y * record.normalScale[1])), "A negative source normalScale.y must really flip tangent-space Y.");
        }

        private static void AssertEmission(GameObject root, MaterialRecord record, Material material, Texture2D source)
        {
            var expected = new Color(record.emissive[0] * record.emissiveIntensity, record.emissive[1] * record.emissiveIntensity, record.emissive[2] * record.emissiveIntensity, 1f);
            var actual = material.GetColor("_EmissionColor");
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.0001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.0001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.0001f));
            Assert.That(material.GetTexture("_EmissionMap"), Is.SameAs(source));
            Assert.That(material.IsKeywordEnabled("_EMISSION"), Is.True);
            AssertTextureST(material, "_EmissionMap", record.emissiveTextureST);

            var instance = UnityEngine.Object.Instantiate(root);
            try
            {
                var player = instance.GetComponent<ThreeUnityAnimationPlayer>();
                Assert.That(player, Is.Not.Null);
                var clip = player.Clips.Single(candidate => candidate.name == player.DefaultClipName);
                var renderer = instance.GetComponentsInChildren<Renderer>(true).Single(candidate => candidate.sharedMaterials.Any(candidateMaterial => candidateMaterial != null && candidateMaterial.name == record.name));
                var slot = Array.FindIndex(renderer.sharedMaterials, candidateMaterial => candidateMaterial != null && candidateMaterial.name == record.name);
                var block = new MaterialPropertyBlock();
                player.Sample(player.DefaultClipName, 0f);
                renderer.GetPropertyBlock(block, slot);
                var start = block.GetColor("_EmissionColor");
                block.Clear();
                player.Sample(player.DefaultClipName, clip.length * 0.5f);
                renderer.GetPropertyBlock(block, slot);
                var middle = block.GetColor("_EmissionColor");
                Assert.That((new Vector3(start.r, start.g, start.b) - new Vector3(middle.r, middle.g, middle.b)).sqrMagnitude, Is.GreaterThan(0.0001f), "The effective materialEmissive animation must change when emissiveIntensity changes.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void AssertTextureST(Material material, string property, float[] st)
        {
            Assert.That(material.GetTextureScale(property), Is.EqualTo(new Vector2(st[0], st[1])).Using(Vector2Comparer));
            Assert.That(material.GetTextureOffset(property), Is.EqualTo(new Vector2(st[2], st[3])).Using(Vector2Comparer));
        }

        private static Vector3 DecodeNormal(Color color) => new Vector3(color.r * 2f - 1f, color.g * 2f - 1f, color.b * 2f - 1f);

        private static void Import(string assetPath) =>
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        private static void DeleteImportedAssets()
        {
            AssetDatabase.DeleteAsset(ImportedSamplePath);
            AssetDatabase.DeleteAsset(FixturePath);
            AssetDatabase.DeleteAsset(LegacyPath);
        }

        private static Document BuildFixture()
        {
            var maskBytes = Enumerable.Repeat(new byte[] { 16, 128, 192, 255 }, 4).SelectMany(value => value).ToArray();
            var normalBytes = Enumerable.Repeat(new byte[] { 160, 208, 245, 255 }, 4).SelectMany(value => value).ToArray();
            var identity = new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f };
            var material = new MaterialRecord
            {
                id = "fixture_material",
                name = "Fixture PBR",
                sourceType = "MeshStandardMaterial",
                baseColor = new[] { 1f, 1f, 1f, 1f },
                emissive = new[] { 0f, 0f, 0f },
                metallic = 0.5f,
                roughness = 0.25f,
                baseColorTextureId = "",
                metalnessTextureId = "fixture_mask",
                roughnessTextureId = "fixture_mask",
                metallicRoughnessTextureId = "fixture_mask",
                normalTextureId = "fixture_normal",
                emissiveTextureId = "",
                baseColorTextureST = IdentitySt(),
                metalnessTextureST = new[] { 1.5f, 1.25f, 0.1f, 0.2f },
                roughnessTextureST = new[] { 1.5f, 1.25f, 0.1f, 0.2f },
                normalTextureST = new[] { 0.8f, 0.9f, 0.05f, 0.15f },
                emissiveTextureST = IdentitySt(),
                normalMapType = "tangent-space",
                normalScale = new[] { 0.75f, 0.75f },
                emissiveIntensity = 1f,
                vertexColors = true,
                renderMode = "surface",
            };
            var lambertMaterial = new MaterialRecord
            {
                id = "fixture_lambert_material",
                name = "Fixture Lambert",
                sourceType = "MeshLambertMaterial",
                baseColor = new[] { 0.75f, 0.5f, 0.25f, 1f },
                emissive = new[] { 0f, 0f, 0f },
                metallic = 0f,
                roughness = 0.5f,
                baseColorTextureId = "",
                metalnessTextureId = "",
                roughnessTextureId = "",
                metallicRoughnessTextureId = "",
                normalTextureId = "",
                emissiveTextureId = "",
                baseColorTextureST = IdentitySt(),
                metalnessTextureST = IdentitySt(),
                roughnessTextureST = IdentitySt(),
                normalTextureST = IdentitySt(),
                emissiveTextureST = IdentitySt(),
                normalMapType = "none",
                normalScale = new[] { 1f, 1f },
                emissiveIntensity = 1f,
                vertexColors = true,
                renderMode = "surface",
            };
            var provided = BuildTriangleMesh("fixture_provided", "Provided Tangents", new[] { 0.6f, 0f, 0.8f, 1f, 0.6f, 0f, 0.8f, 1f, 0.6f, 0f, 0.8f, 1f });
            var recalculated = BuildTriangleMesh("fixture_recalculated", "Recalculated Tangents", Array.Empty<float>());
            var lambertMesh = BuildTriangleMesh("fixture_lambert", "Lambert Approximation", Array.Empty<float>(), lambertMaterial.id);
            return new Document
            {
                format = "three-unity-scene",
                version = 8,
                name = "PBR Import Fixture",
                unitScaleMeters = 1f,
                textures = new[]
                {
                    BuildRawTexture("fixture_mask", "Fixture Mask", maskBytes),
                    BuildRawTexture("fixture_normal", "Fixture Normal", normalBytes),
                },
                materials = new[] { material, lambertMaterial },
                meshes = new[] { provided, recalculated, lambertMesh },
                instancedMeshes = new[]
                {
                    new InstancedMeshRecord { id = "fixture_instances", name = "Fixture Instances", meshId = provided.id, count = 1, matrices = identity, colors = new[] { 1f, 0.8f, 0.6f, 1f } },
                    new InstancedMeshRecord { id = "fixture_lambert_instances", name = "Fixture Lambert Instances", meshId = lambertMesh.id, count = 1, matrices = identity, colors = new[] { 0.8f, 1f, 0.6f, 1f } },
                },
                nodes = new[]
                {
                    BuildNode("fixture_regular_node", "Fixture Regular", recalculated.id, ""),
                    BuildNode("fixture_instanced_node", "Fixture Instanced", "", "fixture_instances"),
                    BuildNode("fixture_lambert_instanced_node", "Fixture Lambert Instanced", "", "fixture_lambert_instances"),
                },
                animations = Array.Empty<AnimationRecord>(),
                warnings = Array.Empty<string>(),
            };
        }

        private static TextureRecord BuildRawTexture(string id, string name, byte[] bytes) => new TextureRecord
        {
            id = id,
            name = name,
            width = 2,
            height = 2,
            encoding = "raw",
            data = Convert.ToBase64String(bytes),
            mimeType = "",
            pixelFormat = "rgba",
            componentType = "uint8",
            flipY = false,
            colorSpace = "none",
            wrapS = "repeat",
            wrapT = "clamp",
            filterMode = "trilinear",
            mipmaps = true,
            anisotropy = 4,
        };

        private static MeshRecord BuildTriangleMesh(string id, string name, float[] tangents, string materialId = "fixture_material") => new MeshRecord
        {
            id = id,
            name = name,
            positions = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f },
            normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f },
            uv0 = new[] { 0f, 0f, 1f, 0f, 0f, 1f },
            tangents = tangents,
            indices = new[] { 0, 1, 2 },
            groups = new[] { new MeshGroupRecord { start = 0, count = 3, materialIndex = 0 } },
            materialIds = new[] { materialId },
        };

        private static NodeRecord BuildNode(string id, string name, string meshId, string instancedMeshId) => new NodeRecord
        {
            id = id,
            name = name,
            visible = true,
            position = new[] { 0f, 0f, 0f },
            quaternion = new[] { 0f, 0f, 0f, 1f },
            scale = new[] { 1f, 1f, 1f },
            layersMask = 1,
            meshId = meshId,
            instancedMeshId = instancedMeshId,
            morphWeights = Array.Empty<float>(),
        };

        private static float[] IdentitySt() => new[] { 1f, 1f, 0f, 0f };

        private static readonly Vector2EqualityComparer Vector2Comparer = new Vector2EqualityComparer();
        private static readonly Vector4EqualityComparer Vector4Comparer = new Vector4EqualityComparer();

        private sealed class Vector2EqualityComparer : System.Collections.Generic.IEqualityComparer<Vector2>
        {
            public bool Equals(Vector2 left, Vector2 right) => (left - right).sqrMagnitude < 0.00000001f;
            public int GetHashCode(Vector2 value) => 0;
        }

        private sealed class Vector4EqualityComparer : System.Collections.Generic.IEqualityComparer<Vector4>
        {
            public bool Equals(Vector4 left, Vector4 right) => (left - right).sqrMagnitude < 0.00000001f;
            public int GetHashCode(Vector4 value) => 0;
        }

        [Serializable]
        private sealed class Document
        {
            public string format;
            public int version;
            public string name;
            public float unitScaleMeters = 1f;
            public NodeRecord[] nodes = Array.Empty<NodeRecord>();
            public MeshRecord[] meshes = Array.Empty<MeshRecord>();
            public InstancedMeshRecord[] instancedMeshes = Array.Empty<InstancedMeshRecord>();
            public MaterialRecord[] materials = Array.Empty<MaterialRecord>();
            public TextureRecord[] textures = Array.Empty<TextureRecord>();
            public AnimationRecord[] animations = Array.Empty<AnimationRecord>();
            public string[] warnings = Array.Empty<string>();
        }

        [Serializable]
        private sealed class NodeRecord
        {
            public string id; public string name; public bool visible = true; public float[] position; public float[] quaternion; public float[] scale; public int layersMask = 1;
            public string meshId; public string instancedMeshId; public float[] morphWeights;
        }

        [Serializable]
        private sealed class MeshRecord
        {
            public string id; public string name; public float[] positions; public float[] normals; public float[] uv0; public float[] tangents; public int[] indices; public MeshGroupRecord[] groups; public string[] materialIds;
        }

        [Serializable] private sealed class MeshGroupRecord { public int start; public int count; public int materialIndex; }
        [Serializable] private sealed class InstancedMeshRecord { public string id; public string name; public string meshId; public int count; public float[] matrices; public float[] colors; }
        [Serializable] private sealed class AnimationRecord { public string id; }

        [Serializable]
        private sealed class MaterialRecord
        {
            public string id; public string name; public string sourceType; public float[] baseColor; public float[] emissive; public float metallic; public float roughness;
            public bool transparent; public bool doubleSided; public float alphaCutoff; public bool unlit; public bool vertexColors;
            public string baseColorTextureId; public string metalnessTextureId; public string roughnessTextureId; public string metallicRoughnessTextureId; public string normalTextureId; public string emissiveTextureId;
            public float[] baseColorTextureST; public float[] metalnessTextureST; public float[] roughnessTextureST; public float[] normalTextureST; public float[] emissiveTextureST;
            public string normalMapType; public float[] normalScale; public float emissiveIntensity = 1f;
            public string renderMode = "surface"; public float pointSize = 1f; public bool sizeAttenuation = true; public float spriteRotation;
        }

        [Serializable]
        private sealed class TextureRecord
        {
            public string id; public string name; public int width; public int height; public string encoding; public string data;
            public string mimeType; public string pixelFormat; public string componentType; public bool flipY; public string colorSpace;
            public string wrapS; public string wrapT; public string filterMode; public bool mipmaps; public int anisotropy;
        }
    }
}

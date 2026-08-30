using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace ThreeUnity.Bridge.Editor
{
    [ScriptedImporter(2, "threeunity")]
    public sealed class ThreeUnityImporter : ScriptedImporter
    {
        [SerializeField] private bool importCameras = true;
        [SerializeField] private bool importLights = true;
        [SerializeField] private bool generateColliders;

        public override void OnImportAsset(AssetImportContext context)
        {
            SceneDocument document;
            try
            {
                document = JsonUtility.FromJson<SceneDocument>(File.ReadAllText(context.assetPath));
            }
            catch (Exception exception)
            {
                context.LogImportError($"Invalid .threeunity JSON: {exception.Message}");
                return;
            }

            if (document == null || document.format != "three-unity-scene" || document.version != 1)
            {
                context.LogImportError("Unsupported document. Expected three-unity-scene format version 1.");
                return;
            }

            var root = new GameObject(string.IsNullOrEmpty(document.name) ? Path.GetFileNameWithoutExtension(context.assetPath) : document.name);
            AttachRuntimeProfile(root, document.runtime);
            var textures = ImportTextures(context, document.textures ?? Array.Empty<TextureRecord>());
            var materials = ImportMaterials(context, document.materials ?? Array.Empty<MaterialRecord>(), textures);
            var meshes = ImportMeshes(context, document.meshes ?? Array.Empty<MeshRecord>(), document.unitScaleMeters);
            var objects = new Dictionary<string, GameObject>();

            foreach (var node in document.nodes ?? Array.Empty<NodeRecord>())
            {
                var gameObject = new GameObject(string.IsNullOrEmpty(node.name) ? "Three.js Node" : node.name);
                gameObject.SetActive(node.visible);
                gameObject.transform.localPosition = ReadPosition(node.position, document.unitScaleMeters);
                gameObject.transform.localRotation = ReadRotation(node.quaternion);
                gameObject.transform.localScale = ReadScale(node.scale);
                var metadata = gameObject.AddComponent<ThreeUnityMetadata>();
                metadata.Initialize(node.id, node.layersMask, node.metadataJson, ConvertComponents(node.components));
                objects[node.id] = gameObject;
            }

            foreach (var node in document.nodes ?? Array.Empty<NodeRecord>())
            {
                if (!objects.TryGetValue(node.id, out var gameObject)) continue;
                var parent = !string.IsNullOrEmpty(node.parentId) && objects.TryGetValue(node.parentId, out var parentObject)
                    ? parentObject.transform
                    : root.transform;
                gameObject.transform.SetParent(parent, false);
                AttachMesh(gameObject, node.meshId, meshes, materials, document.meshes);
                if (importCameras && node.camera != null && !string.IsNullOrEmpty(node.camera.type))
                    AttachCamera(gameObject, node.camera, document.unitScaleMeters);
                if (importLights && node.light != null && !string.IsNullOrEmpty(node.light.type))
                    AttachLight(gameObject, node.light, document.unitScaleMeters);
            }

            foreach (var warning in document.warnings ?? Array.Empty<string>()) context.LogImportWarning(warning);
            context.AddObjectToAsset("root", root);
            context.SetMainObject(root);
        }

        private static void AttachRuntimeProfile(GameObject root, RuntimeRecord record)
        {
            record ??= new RuntimeRecord();
            var sourceItems = record.hotbar ?? Array.Empty<HotbarItemRecord>();
            var items = new ThreeUnityHotbarItem[sourceItems.Length];
            for (var index = 0; index < sourceItems.Length; index++)
            {
                items[index] = new ThreeUnityHotbarItem
                {
                    name = sourceItems[index].name,
                    color = ReadColor(sourceItems[index].color, Color.white),
                };
            }
            root.AddComponent<ThreeUnityRuntimeProfile>().Initialize(
                record.controller, record.colliderMode, record.enableBlockEditing,
                record.allowFly, record.hudStyle, record.moveSpeed,
                record.sprintSpeed, record.flySpeed, items);
        }

        private Dictionary<string, Texture2D> ImportTextures(AssetImportContext context, TextureRecord[] records)
        {
            var result = new Dictionary<string, Texture2D>();
            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.id) || string.IsNullOrEmpty(record.data)) continue;
                try
                {
                    Texture2D texture;
                    if (record.encoding == "rgba8")
                    {
                        texture = new Texture2D(Math.Max(1, record.width), Math.Max(1, record.height), TextureFormat.RGBA32, true);
                        texture.LoadRawTextureData(Convert.FromBase64String(record.data));
                        texture.Apply(true, false);
                    }
                    else
                    {
                        var comma = record.data.IndexOf(',');
                        var payload = comma >= 0 ? record.data.Substring(comma + 1) : record.data;
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                        if (!texture.LoadImage(Convert.FromBase64String(payload), false)) throw new InvalidDataException("Texture image decoder rejected data.");
                    }
                    texture.name = string.IsNullOrEmpty(record.name) ? record.id : record.name;
                    context.AddObjectToAsset(record.id, texture);
                    result[record.id] = texture;
                }
                catch (Exception exception)
                {
                    context.LogImportWarning($"Texture '{record.name}' was skipped: {exception.Message}");
                }
            }
            return result;
        }

        private Dictionary<string, Material> ImportMaterials(AssetImportContext context, MaterialRecord[] records, Dictionary<string, Texture2D> textures)
        {
            var result = new Dictionary<string, Material>();
            foreach (var record in records)
            {
                var shader = FindShader(record.unlit, record.vertexColors);
                if (shader == null)
                {
                    context.LogImportError("No compatible Lit or Unlit shader is available.");
                    continue;
                }
                var material = new Material(shader) { name = string.IsNullOrEmpty(record.name) ? record.id : record.name };
                var color = ReadColor(record.baseColor, Color.white);
                SetColor(material, "_BaseColor", "_Color", color);
                SetFloat(material, "_Metallic", record.metallic);
                SetFloat(material, "_Smoothness", 1f - Mathf.Clamp01(record.roughness));
                SetFloat(material, "_Cutoff", record.alphaCutoff);
                SetFloat(material, "_Unlit", record.unlit ? 1f : 0f);
                SetTexture(material, "_BaseMap", "_MainTex", record.baseColorTextureId, textures);
                if (!string.IsNullOrEmpty(record.normalTextureId) && textures.ContainsKey(record.normalTextureId))
                {
                    material.EnableKeyword("_NORMALMAP");
                    SetTexture(material, "_BumpMap", "_BumpMap", record.normalTextureId, textures);
                }
                if (ReadColor(record.emissive, Color.black).maxColorComponent > 0f)
                {
                    material.EnableKeyword("_EMISSION");
                    SetColor(material, "_EmissionColor", "_EmissionColor", ReadColor(record.emissive, Color.black));
                    SetTexture(material, "_EmissionMap", "_EmissionMap", record.emissiveTextureId, textures);
                }
                ConfigureSurface(material, record.transparent, record.doubleSided);
                context.AddObjectToAsset(record.id, material);
                result[record.id] = material;
            }
            return result;
        }

        private Dictionary<string, Mesh> ImportMeshes(AssetImportContext context, MeshRecord[] records, float unitScale)
        {
            var result = new Dictionary<string, Mesh>();
            foreach (var record in records)
            {
                if (record.positions == null || record.positions.Length < 3)
                {
                    context.LogImportWarning($"Mesh '{record.name}' has no vertices and was skipped.");
                    continue;
                }
                var mesh = new Mesh { name = string.IsNullOrEmpty(record.name) ? record.id : record.name };
                var vertexCount = record.positions.Length / 3;
                if (vertexCount > 65535) mesh.indexFormat = IndexFormat.UInt32;
                mesh.vertices = ReadVectors3(record.positions, true, unitScale);
                if (record.normals != null && record.normals.Length == record.positions.Length) mesh.normals = ReadVectors3(record.normals, true, 1f);
                if (record.uv0 != null && record.uv0.Length / 2 == vertexCount) mesh.uv = ReadVectors2(record.uv0);
                if (record.colors != null && record.colors.Length / 4 == vertexCount) mesh.colors = ReadColors(record.colors);

                var indices = record.indices != null && record.indices.Length > 0 ? (int[])record.indices.Clone() : SequentialIndices(vertexCount);
                // Mirroring Z changes handedness and reverses every triangle. Unity's
                // front-face convention therefore needs one matching index reversal.
                FlipTriangleWinding(indices);
                var groups = record.groups ?? Array.Empty<MeshGroupRecord>();
                if (groups.Length == 0)
                {
                    mesh.subMeshCount = 1;
                    mesh.SetTriangles(indices, 0, true);
                }
                else
                {
                    mesh.subMeshCount = groups.Length;
                    for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                    {
                        var group = groups[groupIndex];
                        var start = Math.Max(0, Math.Min(group.start, indices.Length));
                        var count = Math.Max(0, Math.Min(group.count, indices.Length - start));
                        var triangles = new int[count];
                        Array.Copy(indices, start, triangles, 0, count);
                        mesh.SetTriangles(triangles, groupIndex, false);
                    }
                }
                if (mesh.normals == null || mesh.normals.Length == 0) mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                context.AddObjectToAsset(record.id, mesh);
                result[record.id] = mesh;
            }
            return result;
        }

        private void AttachMesh(GameObject gameObject, string meshId, Dictionary<string, Mesh> meshes, Dictionary<string, Material> materials, MeshRecord[] meshRecords)
        {
            if (string.IsNullOrEmpty(meshId) || !meshes.TryGetValue(meshId, out var mesh)) return;
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            MeshRecord record = null;
            if (meshRecords != null)
            {
                foreach (var candidate in meshRecords) if (candidate.id == meshId) { record = candidate; break; }
            }
            if (record?.materialIds != null && record.materialIds.Length > 0)
            {
                var groups = record.groups ?? Array.Empty<MeshGroupRecord>();
                var shared = new Material[Math.Max(1, groups.Length)];
                for (var index = 0; index < shared.Length; index++)
                {
                    var materialIndex = groups.Length > 0 ? groups[index].materialIndex : 0;
                    materialIndex = Math.Max(0, Math.Min(materialIndex, record.materialIds.Length - 1));
                    materials.TryGetValue(record.materialIds[materialIndex], out shared[index]);
                }
                renderer.sharedMaterials = shared;
            }
            if (generateColliders) gameObject.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void AttachCamera(GameObject gameObject, CameraRecord record, float unitScale)
        {
            var camera = gameObject.AddComponent<Camera>();
            camera.nearClipPlane = record.near * unitScale;
            camera.farClipPlane = record.far * unitScale;
            camera.orthographic = record.type == "orthographic";
            if (camera.orthographic) camera.orthographicSize = Mathf.Abs(record.top - record.bottom) * unitScale * 0.5f;
            else camera.fieldOfView = record.fov;
        }

        private static void AttachLight(GameObject gameObject, LightRecord record, float unitScale)
        {
            var color = ReadColor(record.color, Color.white);
            if (record.type == "ambient")
            {
                var ambient = gameObject.AddComponent<ThreeUnityAmbientLight>();
                ambient.color = color;
                ambient.intensity = record.intensity;
                return;
            }
            var light = gameObject.AddComponent<Light>();
            light.type = record.type == "directional" ? LightType.Directional : record.type == "spot" ? LightType.Spot : LightType.Point;
            light.color = color;
            light.intensity = record.intensity;
            light.range = record.range > 0f ? record.range * unitScale : 10f * unitScale;
            light.spotAngle = record.spotAngleRadians * Mathf.Rad2Deg;
            light.innerSpotAngle = light.spotAngle * (1f - Mathf.Clamp01(record.penumbra));
            light.shadows = record.castShadow ? LightShadows.Soft : LightShadows.None;
        }

        private static Shader FindShader(bool unlit, bool vertexColors)
        {
            if (vertexColors)
            {
                var vertexColorShader = Shader.Find("ThreeUnity/Vertex Color");
                if (vertexColorShader != null) return vertexColorShader;
            }
            if (unlit) return Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            return Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        }

        private static void ConfigureSurface(Material material, bool transparent, bool doubleSided)
        {
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", transparent ? 1f : 0f);
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", transparent ? 3f : 0f);
            if (transparent)
            {
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)RenderQueue.Transparent;
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.EnableKeyword("_ALPHABLEND_ON");
                if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            }
        }

        private static void SetColor(Material material, string preferred, string fallback, Color value)
        {
            if (material.HasProperty(preferred)) material.SetColor(preferred, value);
            else if (material.HasProperty(fallback)) material.SetColor(fallback, value);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetTexture(Material material, string preferred, string fallback, string id, Dictionary<string, Texture2D> textures)
        {
            if (string.IsNullOrEmpty(id) || !textures.TryGetValue(id, out var texture)) return;
            if (material.HasProperty(preferred)) material.SetTexture(preferred, texture);
            else if (material.HasProperty(fallback)) material.SetTexture(fallback, texture);
        }

        private static ThreeUnityComponentDescriptor[] ConvertComponents(ComponentRecord[] records)
        {
            if (records == null) return Array.Empty<ThreeUnityComponentDescriptor>();
            var result = new ThreeUnityComponentDescriptor[records.Length];
            for (var index = 0; index < records.Length; index++)
            {
                result[index] = new ThreeUnityComponentDescriptor { type = records[index].type, dataJson = records[index].dataJson };
            }
            return result;
        }

        private static Vector3 ReadPosition(float[] values, float unitScale) => values != null && values.Length >= 3 ? new Vector3(values[0], values[1], -values[2]) * unitScale : Vector3.zero;
        private static Quaternion ReadRotation(float[] values) => values != null && values.Length >= 4 ? new Quaternion(-values[0], -values[1], values[2], values[3]) : Quaternion.identity;
        private static Vector3 ReadScale(float[] values) => values != null && values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : Vector3.one;

        private static Vector3[] ReadVectors3(float[] values, bool flipZ, float scale)
        {
            var output = new Vector3[values.Length / 3];
            for (var index = 0; index < output.Length; index++) output[index] = new Vector3(values[index * 3], values[index * 3 + 1], flipZ ? -values[index * 3 + 2] : values[index * 3 + 2]) * scale;
            return output;
        }

        private static Vector2[] ReadVectors2(float[] values)
        {
            var output = new Vector2[values.Length / 2];
            for (var index = 0; index < output.Length; index++) output[index] = new Vector2(values[index * 2], values[index * 2 + 1]);
            return output;
        }

        private static Color[] ReadColors(float[] values)
        {
            var output = new Color[values.Length / 4];
            for (var index = 0; index < output.Length; index++) output[index] = new Color(values[index * 4], values[index * 4 + 1], values[index * 4 + 2], values[index * 4 + 3]);
            return output;
        }

        private static Color ReadColor(float[] values, Color fallback) => values != null && values.Length >= 3 ? new Color(values[0], values[1], values[2], values.Length >= 4 ? values[3] : 1f) : fallback;

        private static int[] SequentialIndices(int count)
        {
            var result = new int[count];
            for (var index = 0; index < count; index++) result[index] = index;
            return result;
        }

        private static void FlipTriangleWinding(int[] indices)
        {
            for (var index = 0; index + 2 < indices.Length; index += 3)
            {
                var temporary = indices[index + 1];
                indices[index + 1] = indices[index + 2];
                indices[index + 2] = temporary;
            }
        }

        [Serializable] private sealed class SceneDocument
        {
            public string format;
            public int version;
            public string name;
            public float unitScaleMeters = 1f;
            public NodeRecord[] nodes;
            public MeshRecord[] meshes;
            public MaterialRecord[] materials;
            public TextureRecord[] textures;
            public RuntimeRecord runtime;
            public string[] warnings;
        }

        [Serializable] private sealed class NodeRecord
        {
            public string id;
            public string name;
            public string parentId;
            public bool visible = true;
            public float[] position;
            public float[] quaternion;
            public float[] scale;
            public int layersMask = 1;
            public string meshId;
            public CameraRecord camera;
            public LightRecord light;
            public string metadataJson;
            public ComponentRecord[] components;
        }

        [Serializable] private sealed class CameraRecord { public string type; public float fov; public float near; public float far; public float top; public float bottom; }
        [Serializable] private sealed class LightRecord { public string type; public float[] color; public float intensity; public float range; public float spotAngleRadians; public float penumbra; public bool castShadow; }
        [Serializable] private sealed class ComponentRecord { public string type; public string dataJson; }
        [Serializable] private sealed class MeshRecord { public string id; public string name; public float[] positions; public float[] normals; public float[] uv0; public float[] colors; public int[] indices; public MeshGroupRecord[] groups; public string[] materialIds; }
        [Serializable] private sealed class MeshGroupRecord { public int start; public int count; public int materialIndex; }
        [Serializable] private sealed class MaterialRecord
        {
            public string id; public string name; public float[] baseColor; public float[] emissive; public float metallic; public float roughness = 0.5f; public bool transparent; public bool doubleSided; public float alphaCutoff; public bool unlit; public bool vertexColors;
            public string baseColorTextureId; public string emissiveTextureId; public string normalTextureId;
        }
        [Serializable] private sealed class TextureRecord { public string id; public string name; public int width; public int height; public string encoding; public string data; }
        [Serializable] private sealed class RuntimeRecord
        {
            public string controller = "none"; public string colliderMode = "none"; public bool enableBlockEditing; public bool allowFly; public string hudStyle = "diagnostic";
            public float moveSpeed = 5.5f; public float sprintSpeed = 9f; public float flySpeed = 8f; public HotbarItemRecord[] hotbar;
        }
        [Serializable] private sealed class HotbarItemRecord { public string name; public float[] color; }
    }
}

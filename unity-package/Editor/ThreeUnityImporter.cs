using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace ThreeUnity.Bridge.Editor
{
    [ScriptedImporter(11, "threeunity")]
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

            if (document == null || document.format != "three-unity-scene" || document.version < 1 || document.version > 7)
            {
                context.LogImportError("Unsupported document. Expected three-unity-scene format version 1, 2, 3, 4, 5, 6, or 7.");
                return;
            }

            var root = new GameObject(string.IsNullOrEmpty(document.name) ? Path.GetFileNameWithoutExtension(context.assetPath) : document.name);
            AttachRuntimeProfile(root, document.runtime);
            if (HasComponentDescriptors(document.nodes)) root.AddComponent<ThreeUnityComponentApplicator>();
            var materialCapabilities = IndexMaterialAnimationCapabilities(document);
            var textures = ImportTextures(context, document.textures ?? Array.Empty<TextureRecord>(), document.version);
            var materialRecords = document.materials ?? Array.Empty<MaterialRecord>();
            var materials = ImportMaterials(context, materialRecords, textures, materialCapabilities, document.version);
            var meshes = ImportMeshes(context, document.meshes ?? Array.Empty<MeshRecord>(), document.unitScaleMeters);
            var primitives = ImportPrimitives(context, document.primitives ?? Array.Empty<PrimitiveRecord>(), materialRecords, document.unitScaleMeters);
            var instancedMeshes = IndexInstancedMeshes(document.version >= 6
                ? document.instancedMeshes ?? Array.Empty<InstancedMeshRecord>()
                : Array.Empty<InstancedMeshRecord>());
            var instancedColorMaterials = new Dictionary<string, Material>();
            var skins = IndexSkins(document.skins ?? Array.Empty<SkinRecord>());
            var objects = new Dictionary<string, GameObject>();
            var importedNames = BuildImportedNodeNames(document.nodes ?? Array.Empty<NodeRecord>(), document.version >= 2);

            foreach (var node in document.nodes ?? Array.Empty<NodeRecord>())
            {
                var gameObject = new GameObject(importedNames[node.id]);
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
            }

            foreach (var node in document.nodes ?? Array.Empty<NodeRecord>())
            {
                if (!objects.TryGetValue(node.id, out var gameObject)) continue;
                var renderableReferenceCount =
                    (string.IsNullOrEmpty(node.meshId) ? 0 : 1) +
                    (string.IsNullOrEmpty(node.primitiveId) ? 0 : 1) +
                    (string.IsNullOrEmpty(node.instancedMeshId) ? 0 : 1);
                if (renderableReferenceCount > 1)
                    throw new InvalidDataException($"Node '{node.id}' cannot reference mesh '{node.meshId}', primitive '{node.primitiveId}', and instanced mesh '{node.instancedMeshId}' at the same time.");
                AttachMesh(context, gameObject, node, meshes, materials, document.meshes, skins, objects, document.unitScaleMeters);
                AttachPrimitive(gameObject, node, primitives, materials);
                AttachInstancedMesh(
                    context,
                    gameObject,
                    node,
                    instancedMeshes,
                    meshes,
                    document.meshes ?? Array.Empty<MeshRecord>(),
                    materials,
                    materialRecords,
                    instancedColorMaterials,
                    document.unitScaleMeters);
                if (importCameras && node.camera != null && !string.IsNullOrEmpty(node.camera.type))
                    AttachCamera(gameObject, node.camera, document.unitScaleMeters);
                if (importLights && node.light != null && !string.IsNullOrEmpty(node.light.type))
                    AttachLight(gameObject, node.light, document.unitScaleMeters);
            }

            if (document.version >= 2)
            {
                var animationRecords = document.animations ?? Array.Empty<AnimationRecord>();
                var clips = ImportAnimations(
                    context,
                    root,
                    objects,
                    animationRecords,
                    document.nodes ?? Array.Empty<NodeRecord>(),
                    document.meshes ?? Array.Empty<MeshRecord>(),
                    document.primitives ?? Array.Empty<PrimitiveRecord>(),
                    materialRecords,
                    document.unitScaleMeters,
                    out var materialAnimationClips);
                AttachAnimationPlayer(root, clips, animationRecords, document.defaultAnimationId, document.autoplayAnimation, materialAnimationClips);
                ApplyAnimatedBounds(root, clips.Values);
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

        private static bool HasComponentDescriptors(NodeRecord[] nodes)
        {
            foreach (var node in nodes ?? Array.Empty<NodeRecord>())
            {
                if (node.components != null && node.components.Length > 0) return true;
            }
            return false;
        }

        private static Dictionary<string, SkinRecord> IndexSkins(SkinRecord[] records)
        {
            var result = new Dictionary<string, SkinRecord>();
            foreach (var record in records) result.Add(record.id, record);
            return result;
        }

        private static Dictionary<string, InstancedMeshRecord> IndexInstancedMeshes(InstancedMeshRecord[] records)
        {
            var result = new Dictionary<string, InstancedMeshRecord>();
            foreach (var record in records)
            {
                if (record == null || string.IsNullOrEmpty(record.id))
                    throw new InvalidDataException("An instanced mesh record is missing its id.");
                result.Add(record.id, record);
            }
            return result;
        }

        private static Dictionary<string, MaterialAnimationCapabilities> IndexMaterialAnimationCapabilities(SceneDocument document)
        {
            var result = new Dictionary<string, MaterialAnimationCapabilities>();
            if (document.version < 4) return result;

            var nodes = new Dictionary<string, NodeRecord>();
            foreach (var node in document.nodes ?? Array.Empty<NodeRecord>()) nodes.Add(node.id, node);
            var meshes = new Dictionary<string, MeshRecord>();
            foreach (var mesh in document.meshes ?? Array.Empty<MeshRecord>()) meshes.Add(mesh.id, mesh);
            var primitives = new Dictionary<string, PrimitiveRecord>();
            foreach (var primitive in document.primitives ?? Array.Empty<PrimitiveRecord>()) primitives.Add(primitive.id, primitive);

            foreach (var animation in document.animations ?? Array.Empty<AnimationRecord>())
            foreach (var track in animation.tracks ?? Array.Empty<AnimationTrackRecord>())
            {
                if (!IsMaterialAnimationProperty(track.property)) continue;
                if (!nodes.TryGetValue(track.targetNodeId, out var node)) continue;
                string[] materialIds;
                if (!string.IsNullOrEmpty(node.meshId) && meshes.TryGetValue(node.meshId, out var mesh))
                    materialIds = mesh.materialIds;
                else if (!string.IsNullOrEmpty(node.primitiveId) && primitives.TryGetValue(node.primitiveId, out var primitive))
                    materialIds = primitive.materialIds;
                else
                    continue;
                if (materialIds == null ||
                    track.materialIndex < 0 ||
                    track.materialIndex >= materialIds.Length)
                    continue;

                var materialId = materialIds[track.materialIndex];
                if (!result.TryGetValue(materialId, out var capabilities))
                {
                    capabilities = new MaterialAnimationCapabilities();
                    result.Add(materialId, capabilities);
                }
                if (track.property == "materialEmissive") capabilities.emission = true;
                if (track.property == "materialBaseColor")
                {
                    var values = track.values ?? Array.Empty<float>();
                    for (var index = 3; index < values.Length; index += 4)
                    {
                        if (values[index] < 0.999f)
                        {
                            capabilities.transparent = true;
                            break;
                        }
                    }
                }
            }
            return result;
        }

        private static Dictionary<string, string> BuildImportedNodeNames(NodeRecord[] nodes, bool makeAnimationPathsSafe)
        {
            var result = new Dictionary<string, string>();
            foreach (var node in nodes)
            {
                var name = string.IsNullOrEmpty(node.name) ? "Three.js Node" : node.name;
                if (makeAnimationPathsSafe)
                    name = $"{EscapeAnimationPathSegment(name)} [{EscapeAnimationPathSegment(node.id)}]";
                result.Add(node.id, name);
            }
            return result;
        }

        private static string EscapeAnimationPathSegment(string value) => value.Replace("%", "%25").Replace("/", "%2F");

        private Dictionary<string, Texture2D> ImportTextures(AssetImportContext context, TextureRecord[] records, int documentVersion)
        {
            return documentVersion >= 7
                ? ImportV7Textures(context, records)
                : ImportLegacyTextures(context, records, documentVersion);
        }

        private Dictionary<string, Texture2D> ImportLegacyTextures(AssetImportContext context, TextureRecord[] records, int documentVersion)
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
                        texture.SetPixelData(Convert.FromBase64String(record.data), 0);
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
                    if (documentVersion >= 4)
                    {
                        var textureName = string.IsNullOrEmpty(record.name) ? record.id : record.name;
                        texture.wrapModeU = ReadTextureWrapMode(record.wrapS, textureName);
                        texture.wrapModeV = ReadTextureWrapMode(record.wrapT, textureName);
                    }
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

        private static Dictionary<string, Texture2D> ImportV7Textures(AssetImportContext context, TextureRecord[] records)
        {
            var result = new Dictionary<string, Texture2D>();
            foreach (var record in records)
            {
                if (record == null) throw new InvalidDataException("A version 7 texture record is null.");
                if (string.IsNullOrEmpty(record.id)) throw new InvalidDataException("A version 7 texture record is missing its id.");
                if (result.ContainsKey(record.id)) throw new InvalidDataException($"Texture id '{record.id}' appears more than once.");
                if (string.IsNullOrEmpty(record.data)) throw new InvalidDataException($"Texture '{TextureLabel(record)}' has an empty payload.");

                ValidateV7TextureSettings(record);
                Texture2D texture;
                switch (record.encoding)
                {
                    case "encoded-image":
                        texture = ImportEncodedImage(record);
                        break;
                    case "raw":
                        texture = ImportRawTexture(record);
                        break;
                    default:
                        throw new InvalidDataException($"Texture '{TextureLabel(record)}' uses unsupported version 7 encoding '{record.encoding}'.");
                }

                texture.name = string.IsNullOrEmpty(record.name) ? record.id : record.name;
                ApplyV7Sampler(context, record, texture);
                result.Add(record.id, texture);
            }

            foreach (var record in records) context.AddObjectToAsset(record.id, result[record.id]);
            return result;
        }

        private static Texture2D ImportEncodedImage(TextureRecord record)
        {
            if (record.mimeType != "image/png" && record.mimeType != "image/jpeg")
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' uses unsupported encoded image MIME type '{record.mimeType}'.");
            if (!string.IsNullOrEmpty(record.pixelFormat) || !string.IsNullOrEmpty(record.componentType))
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' must leave pixelFormat and componentType empty for encoded-image data.");
            if (record.width < 0 || record.height < 0)
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' has invalid encoded image dimensions {record.width}x{record.height}.");

            var payload = DecodeTexturePayload(record);
            ValidateEncodedImageMagic(record, payload);
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!decoded.LoadImage(payload, false))
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' image decoder rejected its {record.mimeType} payload.");

            var bytesPerPixel = ReadDecodedBytesPerPixel(record, decoded.format);
            var baseLevelByteCount = CheckedByteCount(record, decoded.width, decoded.height, bytesPerPixel);
            var textureData = decoded.GetRawTextureData<byte>();
            if (textureData.Length < baseLevelByteCount)
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' decoded to {decoded.width}x{decoded.height} {decoded.format}, but its raw storage contains only {textureData.Length} bytes; expected at least {baseLevelByteCount}.");
            var baseLevel = new byte[baseLevelByteCount];
            for (var index = 0; index < baseLevel.Length; index++) baseLevel[index] = textureData[index];
            NormalizeRowsForUnity(baseLevel, decoded.width, decoded.height, bytesPerPixel, record.flipY, TexturePayloadKind.EncodedImage);
            var texture = new Texture2D(decoded.width, decoded.height, decoded.format, record.mipmaps, ReadTextureLinear(record));
            texture.SetPixelData(baseLevel, 0);
            texture.Apply(record.mipmaps, false);
            UnityEngine.Object.DestroyImmediate(decoded);
            return texture;
        }

        private static Texture2D ImportRawTexture(TextureRecord record)
        {
            if (!string.IsNullOrEmpty(record.mimeType))
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' must leave mimeType empty for raw data.");
            if (record.width <= 0 || record.height <= 0)
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' has invalid raw dimensions {record.width}x{record.height}.");

            var channelCount = ReadRawChannelCount(record);
            var bytesPerComponent = ReadRawBytesPerComponent(record);
            var sourceBytesPerPixel = checked(channelCount * bytesPerComponent);
            var expectedByteCount = CheckedByteCount(record, record.width, record.height, sourceBytesPerPixel);
            var payload = DecodeTexturePayload(record);
            if (payload.Length != expectedByteCount)
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' raw {record.width}x{record.height} {record.pixelFormat}/{record.componentType} payload has {payload.Length} bytes; expected exactly {expectedByteCount}.");

            NormalizeRowsForUnity(payload, record.width, record.height, sourceBytesPerPixel, record.flipY, TexturePayloadKind.Raw);
            var textureFormat = ReadRawTextureFormat(record);
            if (record.pixelFormat == "rgb" && record.componentType != "uint8")
                payload = ExpandRawRgbToRgba(payload, record.width, record.height, bytesPerComponent);
            if (!SystemInfo.SupportsTextureFormat(textureFormat))
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' requires unsupported Unity texture format {textureFormat} for {record.pixelFormat}/{record.componentType} data.");

            var texture = new Texture2D(record.width, record.height, textureFormat, record.mipmaps, ReadTextureLinear(record));
            texture.SetPixelData(payload, 0);
            texture.Apply(record.mipmaps, false);
            return texture;
        }

        private static void ValidateV7TextureSettings(TextureRecord record)
        {
            ReadTextureLinear(record);
            ReadTextureWrapMode(record.wrapS, TextureLabel(record));
            ReadTextureWrapMode(record.wrapT, TextureLabel(record));
            ReadTextureFilterMode(record);
            if (!record.mipmaps && record.filterMode == "trilinear")
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' requests trilinear filtering without mipmaps.");
            if (record.anisotropy < 1)
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' has invalid anisotropy {record.anisotropy}; expected an integer of at least 1.");
        }

        private static byte[] DecodeTexturePayload(TextureRecord record)
        {
            try
            {
                var payload = Convert.FromBase64String(record.data);
                if (payload.Length == 0) throw new InvalidDataException($"Texture '{TextureLabel(record)}' decoded to an empty payload.");
                return payload;
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' payload is not valid base64.", exception);
            }
        }

        private static void ValidateEncodedImageMagic(TextureRecord record, byte[] payload)
        {
            var isPng = payload.Length >= 8 &&
                        payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4e && payload[3] == 0x47 &&
                        payload[4] == 0x0d && payload[5] == 0x0a && payload[6] == 0x1a && payload[7] == 0x0a;
            var isJpeg = payload.Length >= 3 && payload[0] == 0xff && payload[1] == 0xd8 && payload[2] == 0xff;
            if ((record.mimeType == "image/png" && !isPng) || (record.mimeType == "image/jpeg" && !isJpeg))
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' payload magic does not match MIME type {record.mimeType}.");
        }

        private static int ReadDecodedBytesPerPixel(TextureRecord record, TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGB24: return 3;
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32: return 4;
                default: throw new InvalidDataException($"Texture '{TextureLabel(record)}' decoded to unsupported Unity texture format {format}.");
            }
        }

        private static int ReadRawChannelCount(TextureRecord record)
        {
            switch (record.pixelFormat)
            {
                case "r": return 1;
                case "rg": return 2;
                case "rgb": return 3;
                case "rgba": return 4;
                default: throw new InvalidDataException($"Texture '{TextureLabel(record)}' uses unsupported raw pixel format '{record.pixelFormat}'.");
            }
        }

        private static int ReadRawBytesPerComponent(TextureRecord record)
        {
            switch (record.componentType)
            {
                case "uint8": return 1;
                case "float16": return 2;
                case "float32": return 4;
                default: throw new InvalidDataException($"Texture '{TextureLabel(record)}' uses unsupported raw component type '{record.componentType}'.");
            }
        }

        private static TextureFormat ReadRawTextureFormat(TextureRecord record)
        {
            switch (record.componentType)
            {
                case "uint8":
                    switch (record.pixelFormat)
                    {
                        case "r": return TextureFormat.R8;
                        case "rg": return TextureFormat.RG16;
                        case "rgb": return TextureFormat.RGB24;
                        case "rgba": return TextureFormat.RGBA32;
                    }
                    break;
                case "float16":
                    switch (record.pixelFormat)
                    {
                        case "r": return TextureFormat.RHalf;
                        case "rg": return TextureFormat.RGHalf;
                        case "rgb":
                        case "rgba": return TextureFormat.RGBAHalf;
                    }
                    break;
                case "float32":
                    switch (record.pixelFormat)
                    {
                        case "r": return TextureFormat.RFloat;
                        case "rg": return TextureFormat.RGFloat;
                        case "rgb":
                        case "rgba": return TextureFormat.RGBAFloat;
                    }
                    break;
            }
            throw new InvalidDataException($"Texture '{TextureLabel(record)}' uses unsupported raw format {record.pixelFormat}/{record.componentType}.");
        }

        private static bool ReadTextureLinear(TextureRecord record)
        {
            switch (record.colorSpace)
            {
                case "srgb": return false;
                case "linear":
                case "none": return true;
                default: throw new InvalidDataException($"Texture '{TextureLabel(record)}' uses unsupported color space '{record.colorSpace}'.");
            }
        }

        private static FilterMode ReadTextureFilterMode(TextureRecord record)
        {
            switch (record.filterMode)
            {
                case "point": return FilterMode.Point;
                case "bilinear": return FilterMode.Bilinear;
                case "trilinear": return FilterMode.Trilinear;
                default: throw new InvalidDataException($"Texture '{TextureLabel(record)}' uses unsupported filter mode '{record.filterMode}'.");
            }
        }

        private static void ApplyV7Sampler(AssetImportContext context, TextureRecord record, Texture2D texture)
        {
            texture.wrapModeU = ReadTextureWrapMode(record.wrapS, TextureLabel(record));
            texture.wrapModeV = ReadTextureWrapMode(record.wrapT, TextureLabel(record));
            texture.filterMode = ReadTextureFilterMode(record);
            if (record.anisotropy > 16)
                context.LogImportWarning($"Texture '{TextureLabel(record)}' anisotropy {record.anisotropy} exceeds Unity's maximum 16 and was clamped to 16.");
            texture.anisoLevel = Math.Min(record.anisotropy, 16);
        }

        private static int CheckedByteCount(TextureRecord record, int width, int height, int bytesPerPixel)
        {
            try
            {
                var value = checked((long)width * height * bytesPerPixel);
                if (value > int.MaxValue) throw new OverflowException();
                return (int)value;
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException($"Texture '{TextureLabel(record)}' dimensions {width}x{height} and {bytesPerPixel} bytes per pixel exceed Unity's supported payload size.", exception);
            }
        }

        private static byte[] ExpandRawRgbToRgba(byte[] source, int width, int height, int bytesPerComponent)
        {
            var pixelCount = checked(width * height);
            var target = new byte[checked(pixelCount * 4 * bytesPerComponent)];
            for (var pixel = 0; pixel < pixelCount; pixel++)
            {
                var sourceOffset = pixel * 3 * bytesPerComponent;
                var targetOffset = pixel * 4 * bytesPerComponent;
                Buffer.BlockCopy(source, sourceOffset, target, targetOffset, 3 * bytesPerComponent);
                if (bytesPerComponent == 2)
                {
                    target[targetOffset + 6] = 0x00;
                    target[targetOffset + 7] = 0x3c;
                }
                else
                {
                    target[targetOffset + 12] = 0x00;
                    target[targetOffset + 13] = 0x00;
                    target[targetOffset + 14] = 0x80;
                    target[targetOffset + 15] = 0x3f;
                }
            }
            return target;
        }

        private static void NormalizeRowsForUnity(
            byte[] payload,
            int width,
            int height,
            int bytesPerPixel,
            bool sourceFlipY,
            TexturePayloadKind kind)
        {
            // Unity's decoded-image rows already follow its bottom-origin texture convention,
            // which matches a Three.js image after the normal flipY=true upload. Raw typed-array
            // row zero already maps to Unity y=0, matching Three.js flipY=false. The two source
            // kinds therefore use opposite predicates, but every payload performs at most one row swap here.
            var swapRows = kind == TexturePayloadKind.EncodedImage ? !sourceFlipY : sourceFlipY;
            if (!swapRows || height < 2) return;
            var rowByteCount = checked(width * bytesPerPixel);
            var temporary = new byte[rowByteCount];
            for (var top = 0; top < height / 2; top++)
            {
                var bottom = height - 1 - top;
                var topOffset = top * rowByteCount;
                var bottomOffset = bottom * rowByteCount;
                Buffer.BlockCopy(payload, topOffset, temporary, 0, rowByteCount);
                Buffer.BlockCopy(payload, bottomOffset, payload, topOffset, rowByteCount);
                Buffer.BlockCopy(temporary, 0, payload, bottomOffset, rowByteCount);
            }
        }

        private static string TextureLabel(TextureRecord record) => string.IsNullOrEmpty(record.name) ? record.id : record.name;

        private enum TexturePayloadKind
        {
            EncodedImage,
            Raw,
        }

        private Dictionary<string, Material> ImportMaterials(
            AssetImportContext context,
            MaterialRecord[] records,
            Dictionary<string, Texture2D> textures,
            Dictionary<string, MaterialAnimationCapabilities> animationCapabilities,
            int documentVersion)
        {
            var result = new Dictionary<string, Material>();
            foreach (var record in records)
            {
                var renderMode = documentVersion >= 5 ? record.renderMode : "surface";
                var shader = FindShader(renderMode, record.unlit, record.vertexColors);
                if (shader == null)
                {
                    throw new InvalidDataException($"Material '{record.id}' render mode '{renderMode}' has no compatible shader.");
                }
                var material = new Material(shader) { name = string.IsNullOrEmpty(record.name) ? record.id : record.name };
                var color = ReadColor(record.baseColor, Color.white);
                SetColor(material, "_BaseColor", "_Color", color);
                SetFloat(material, "_Metallic", record.metallic);
                SetFloat(material, "_Smoothness", 1f - Mathf.Clamp01(record.roughness));
                SetFloat(material, "_Glossiness", 1f - Mathf.Clamp01(record.roughness));
                SetFloat(material, "_Cutoff", record.alphaCutoff);
                SetFloat(material, "_Unlit", record.unlit ? 1f : 0f);
                SetFloat(material, "_UseVertexColor", record.vertexColors ? 1f : 0f);
                SetFloat(material, "_PointSize", record.pointSize);
                SetFloat(material, "_SizeAttenuation", record.sizeAttenuation ? 1f : 0f);
                SetFloat(material, "_SpriteRotation", record.spriteRotation);
                SetFloat(material, "_BillboardMode", renderMode == "sprite" ? 1f : 0f);
                SetTexture(material, "_BaseMap", "_MainTex", record.baseColorTextureId, textures);
                ApplyBaseMapST(material, ReadBaseMapST(record.baseColorTextureST));
                if (!string.IsNullOrEmpty(record.normalTextureId) && textures.ContainsKey(record.normalTextureId))
                {
                    material.EnableKeyword("_NORMALMAP");
                    SetTexture(material, "_BumpMap", "_BumpMap", record.normalTextureId, textures);
                }
                animationCapabilities.TryGetValue(record.id, out var capabilities);
                if (ReadColor(record.emissive, Color.black).maxColorComponent > 0f || capabilities?.emission == true)
                {
                    SetColor(material, "_EmissionColor", "_EmissionColor", ReadColor(record.emissive, Color.black));
                    SetTexture(material, "_EmissionMap", "_EmissionMap", record.emissiveTextureId, textures);
                    if (documentVersion >= 7)
                        material.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                    material.EnableKeyword("_EMISSION");
                }
                ConfigureSurface(material, record.transparent || capabilities?.transparent == true, record.doubleSided);
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
                if ((record.skinIndices != null && record.skinIndices.Length > 0) || (record.skinWeights != null && record.skinWeights.Length > 0))
                {
                    if (record.skinIndices == null || record.skinWeights == null || record.skinIndices.Length != vertexCount * 4 || record.skinWeights.Length != vertexCount * 4)
                        throw new InvalidDataException($"Mesh '{record.id}' must provide exactly four skin indices and weights per vertex.");
                    mesh.boneWeights = ReadBoneWeights(record.skinIndices, record.skinWeights, vertexCount);
                }

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
                AddBlendShapes(mesh, record, vertexCount, unitScale);
                mesh.RecalculateBounds();
                context.AddObjectToAsset(record.id, mesh);
                result[record.id] = mesh;
            }
            return result;
        }

        private Dictionary<string, ImportedPrimitive> ImportPrimitives(
            AssetImportContext context,
            PrimitiveRecord[] records,
            MaterialRecord[] materialRecords,
            float unitScale)
        {
            var result = new Dictionary<string, ImportedPrimitive>();
            var materialsById = new Dictionary<string, MaterialRecord>();
            foreach (var material in materialRecords) materialsById.Add(material.id, material);

            foreach (var record in records)
            {
                Mesh mesh;
                switch (record.type)
                {
                    case "line":
                    case "line-segments":
                    case "line-loop":
                        mesh = ImportLinePrimitive(record, unitScale);
                        break;
                    case "points":
                        mesh = ImportPointsPrimitive(record, materialsById, unitScale);
                        break;
                    case "sprite":
                        mesh = ImportSpritePrimitive(record, unitScale);
                        break;
                    default:
                        throw new InvalidDataException($"Primitive '{record.id}' uses unsupported type '{record.type}'.");
                }
                context.AddObjectToAsset(record.id, mesh);
                result.Add(record.id, new ImportedPrimitive(record, mesh));
            }
            return result;
        }

        private static Mesh ImportLinePrimitive(PrimitiveRecord record, float unitScale)
        {
            var positions = record.positions ?? Array.Empty<float>();
            if (positions.Length == 0 || positions.Length % 3 != 0)
                throw new InvalidDataException($"Line primitive '{record.id}' must provide complete XYZ positions.");
            var indices = record.indices ?? Array.Empty<int>();
            if (indices.Length % 2 != 0)
                throw new InvalidDataException($"Line primitive '{record.id}' must provide an even canonical index count, but found {indices.Length}.");

            var mesh = new Mesh { name = string.IsNullOrEmpty(record.name) ? record.id : record.name };
            var vertexCount = positions.Length / 3;
            if (vertexCount > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = ReadVectors3(positions, true, unitScale);
            if (record.colors != null && record.colors.Length == vertexCount * 4) mesh.colors = ReadColors(record.colors);

            var groups = record.groups ?? Array.Empty<MeshGroupRecord>();
            mesh.subMeshCount = Math.Max(1, groups.Length);
            if (groups.Length == 0)
            {
                mesh.SetIndices(indices, MeshTopology.Lines, 0, false);
            }
            else
            {
                for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                {
                    if (groups[groupIndex].count % 2 != 0)
                        throw new InvalidDataException($"Line primitive '{record.id}' group {groupIndex} has odd canonical index count {groups[groupIndex].count}.");
                    mesh.SetIndices(ReadGroupIndices(record, groups[groupIndex], indices), MeshTopology.Lines, groupIndex, false);
                }
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh ImportPointsPrimitive(
            PrimitiveRecord record,
            Dictionary<string, MaterialRecord> materialsById,
            float unitScale)
        {
            var positions = record.positions ?? Array.Empty<float>();
            if (positions.Length == 0 || positions.Length % 3 != 0)
                throw new InvalidDataException($"Points primitive '{record.id}' must provide complete XYZ positions.");
            var sourceCenters = ReadVectors3(positions, true, unitScale);
            var sourceColors = record.colors != null && record.colors.Length == sourceCenters.Length * 4
                ? ReadColors(record.colors)
                : null;
            var sourceIndices = record.indices ?? Array.Empty<int>();
            var sourceGroups = record.groups ?? Array.Empty<MeshGroupRecord>();
            var groups = sourceGroups.Length > 0
                ? sourceGroups
                : new[] { new MeshGroupRecord { start = 0, count = sourceIndices.Length, materialIndex = 0 } };

            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var corners = new List<Vector2>();
            var colors = new List<Color>();
            var submeshIndices = new List<int>[groups.Length];
            var quadUv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            var quadCorners = new[] { new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f) };

            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var groupIndices = ReadGroupIndices(record, groups[groupIndex], sourceIndices);
                var triangles = new List<int>(groupIndices.Length * 6);
                foreach (var sourceIndex in groupIndices)
                {
                    if (sourceIndex < 0 || sourceIndex >= sourceCenters.Length)
                        throw new InvalidDataException($"Points primitive '{record.id}' references vertex {sourceIndex}, but has {sourceCenters.Length} vertices.");
                    var vertexStart = vertices.Count;
                    var color = sourceColors == null ? Color.white : sourceColors[sourceIndex];
                    for (var cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                    {
                        vertices.Add(sourceCenters[sourceIndex]);
                        uv.Add(quadUv[cornerIndex]);
                        corners.Add(quadCorners[cornerIndex]);
                        colors.Add(color);
                    }
                    triangles.Add(vertexStart);
                    triangles.Add(vertexStart + 1);
                    triangles.Add(vertexStart + 2);
                    triangles.Add(vertexStart);
                    triangles.Add(vertexStart + 2);
                    triangles.Add(vertexStart + 3);
                }
                submeshIndices[groupIndex] = triangles;
            }

            var mesh = new Mesh { name = string.IsNullOrEmpty(record.name) ? record.id : record.name };
            if (vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, corners);
            mesh.SetColors(colors);
            mesh.subMeshCount = groups.Length;
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                mesh.SetIndices(submeshIndices[groupIndex].ToArray(), MeshTopology.Triangles, groupIndex, false);
            mesh.RecalculateBounds();
            mesh.bounds = ExpandPointBounds(mesh.bounds, record, materialsById, unitScale);
            return mesh;
        }

        private static Mesh ImportSpritePrimitive(PrimitiveRecord record, float unitScale)
        {
            var center = record.spriteCenter ?? Array.Empty<float>();
            if (center.Length != 2)
                throw new InvalidDataException($"Sprite primitive '{record.id}' must provide exactly two spriteCenter values.");
            var uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            var vertices = new Vector3[4];
            var radius = 0f;
            for (var index = 0; index < vertices.Length; index++)
            {
                vertices[index] = new Vector3(uv[index].x - center[0], uv[index].y - center[1], 0f) * unitScale;
                radius = Mathf.Max(radius, vertices[index].magnitude);
            }

            var mesh = new Mesh { name = string.IsNullOrEmpty(record.name) ? record.id : record.name };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * Mathf.Max(radius * 2f, 0.001f));
            return mesh;
        }

        private static int[] ReadGroupIndices(PrimitiveRecord primitive, MeshGroupRecord group, int[] indices)
        {
            if (group.start < 0 || group.count < 0 || group.start + group.count > indices.Length)
                throw new InvalidDataException($"Primitive '{primitive.id}' group start {group.start} count {group.count} exceeds canonical index count {indices.Length}.");
            var result = new int[group.count];
            Array.Copy(indices, group.start, result, 0, group.count);
            return result;
        }

        private static Bounds ExpandPointBounds(
            Bounds bounds,
            PrimitiveRecord primitive,
            Dictionary<string, MaterialRecord> materialsById,
            float unitScale)
        {
            var maximumPointSize = 1f;
            foreach (var materialId in primitive.materialIds ?? Array.Empty<string>())
            {
                if (materialsById.TryGetValue(materialId, out var material))
                    maximumPointSize = Mathf.Max(maximumPointSize, material.pointSize);
            }
            var padding = Mathf.Max(0.001f, maximumPointSize * unitScale * 0.5f);
            bounds.Expand(padding * 2f);
            return bounds;
        }

        private static void AddBlendShapes(Mesh mesh, MeshRecord record, int vertexCount, float unitScale)
        {
            var targets = record.morphTargets ?? Array.Empty<MorphTargetRecord>();
            var names = new HashSet<string>();
            var meshName = string.IsNullOrEmpty(record.name) ? record.id : record.name;
            foreach (var target in targets)
            {
                if (target == null || string.IsNullOrEmpty(target.name))
                    throw new InvalidDataException($"Mesh '{meshName}' contains a morph target without a name.");
                if (!names.Add(target.name))
                    throw new InvalidDataException($"Mesh '{meshName}' contains duplicate morph target name '{target.name}'.");

                var positionDeltas = target.positionDeltas ?? Array.Empty<float>();
                if (positionDeltas.Length != vertexCount * 3)
                    throw new InvalidDataException($"Mesh '{meshName}' morph target '{target.name}' has {positionDeltas.Length} position delta values for {vertexCount} vertices.");
                var normalDeltas = target.normalDeltas ?? Array.Empty<float>();
                if (normalDeltas.Length != 0 && normalDeltas.Length != vertexCount * 3)
                    throw new InvalidDataException($"Mesh '{meshName}' morph target '{target.name}' has {normalDeltas.Length} normal delta values for {vertexCount} vertices.");

                mesh.AddBlendShapeFrame(
                    target.name,
                    100f,
                    ReadVectors3(positionDeltas, true, unitScale),
                    normalDeltas.Length == 0 ? null : ReadVectors3(normalDeltas, true, 1f),
                    null);
            }
        }

        private void AttachMesh(
            AssetImportContext context,
            GameObject gameObject,
            NodeRecord node,
            Dictionary<string, Mesh> meshes,
            Dictionary<string, Material> materials,
            MeshRecord[] meshRecords,
            Dictionary<string, SkinRecord> skins,
            Dictionary<string, GameObject> objects,
            float unitScale)
        {
            if (string.IsNullOrEmpty(node.meshId) || !meshes.TryGetValue(node.meshId, out var mesh)) return;
            MeshRecord record = null;
            if (meshRecords != null)
            {
                foreach (var candidate in meshRecords) if (candidate.id == node.meshId) { record = candidate; break; }
            }
            var morphWeights = node.morphWeights ?? Array.Empty<float>();
            if (morphWeights.Length != mesh.blendShapeCount)
                throw new InvalidDataException($"Node '{node.id}' provides {morphWeights.Length} morph weights for mesh '{node.meshId}' with {mesh.blendShapeCount} morph targets.");

            if (!string.IsNullOrEmpty(node.skinId))
            {
                if (!skins.TryGetValue(node.skinId, out var skin)) throw new InvalidDataException($"Node '{node.id}' references missing skin '{node.skinId}'.");
                AttachSkinnedMesh(context, gameObject, node, mesh, record, skin, materials, objects, unitScale);
                return;
            }

            if (mesh.blendShapeCount > 0)
            {
                AttachMorphMesh(gameObject, node, mesh, record, materials);
                return;
            }

            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            AssignMaterials(renderer, record, materials);
            if (generateColliders) gameObject.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void AttachPrimitive(
            GameObject gameObject,
            NodeRecord node,
            Dictionary<string, ImportedPrimitive> primitives,
            Dictionary<string, Material> materials)
        {
            if (string.IsNullOrEmpty(node.primitiveId)) return;
            if (!primitives.TryGetValue(node.primitiveId, out var primitive))
                throw new InvalidDataException($"Node '{node.id}' references missing primitive '{node.primitiveId}'.");
            gameObject.AddComponent<MeshFilter>().sharedMesh = primitive.mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            AssignPrimitiveMaterials(renderer, primitive.record, materials);
        }

        private void AttachInstancedMesh(
            AssetImportContext context,
            GameObject gameObject,
            NodeRecord node,
            Dictionary<string, InstancedMeshRecord> instancedMeshes,
            Dictionary<string, Mesh> meshes,
            MeshRecord[] meshRecords,
            Dictionary<string, Material> materials,
            MaterialRecord[] materialRecords,
            Dictionary<string, Material> instancedColorMaterials,
            float unitScale)
        {
            if (string.IsNullOrEmpty(node.instancedMeshId)) return;
            if (!instancedMeshes.TryGetValue(node.instancedMeshId, out var record))
                throw new InvalidDataException($"Node '{node.id}' references missing instanced mesh '{node.instancedMeshId}'.");
            if (record.count < 0)
                throw new InvalidDataException($"Instanced mesh '{record.id}' has negative count {record.count}.");
            if (string.IsNullOrEmpty(record.meshId) || !meshes.TryGetValue(record.meshId, out var mesh))
                throw new InvalidDataException($"Instanced mesh '{record.id}' references missing mesh '{record.meshId}'.");

            MeshRecord meshRecord = null;
            foreach (var candidate in meshRecords)
            {
                if (candidate.id == record.meshId)
                {
                    meshRecord = candidate;
                    break;
                }
            }
            if (meshRecord == null)
                throw new InvalidDataException($"Instanced mesh '{record.id}' references missing mesh record '{record.meshId}'.");
            if ((meshRecord.skinIndices != null && meshRecord.skinIndices.Length > 0) ||
                (meshRecord.skinWeights != null && meshRecord.skinWeights.Length > 0))
                throw new InvalidDataException($"Instanced mesh '{record.id}' cannot use skinned mesh '{record.meshId}'. Export it with instancedMeshMode: \"expanded\".");
            if (meshRecord.morphTargets != null && meshRecord.morphTargets.Length > 0)
                throw new InvalidDataException($"Instanced mesh '{record.id}' cannot use morph mesh '{record.meshId}'. Export it with instancedMeshMode: \"expanded\".");
            if (!string.IsNullOrEmpty(node.skinId))
                throw new InvalidDataException($"Instanced node '{node.id}' cannot reference skin '{node.skinId}'. Export it with instancedMeshMode: \"expanded\".");
            if (node.morphWeights != null && node.morphWeights.Length > 0)
                throw new InvalidDataException($"Instanced node '{node.id}' cannot provide morph weights. Export it with instancedMeshMode: \"expanded\".");

            var sourceMatrices = record.matrices ?? Array.Empty<float>();
            var expectedMatrixValueCount = (long)record.count * 16;
            if (sourceMatrices.LongLength != expectedMatrixValueCount)
                throw new InvalidDataException($"Instanced mesh '{record.id}' has {sourceMatrices.LongLength} matrix values for {record.count} instances; expected {expectedMatrixValueCount}.");
            var localMatrices = new Matrix4x4[record.count];
            const float affineTolerance = 0.00001f;
            for (var index = 0; index < localMatrices.Length; index++)
            {
                var source = ReadThreeMatrix(sourceMatrices, index * 16);
                for (var row = 0; row < 4; row++)
                for (var column = 0; column < 4; column++)
                {
                    if (!IsFinite(source[row, column]))
                        throw new InvalidDataException($"Instanced mesh '{record.id}' matrix {index} contains a non-finite value at row {row}, column {column}.");
                }
                if (Mathf.Abs(source.m30) > affineTolerance ||
                    Mathf.Abs(source.m31) > affineTolerance ||
                    Mathf.Abs(source.m32) > affineTolerance ||
                    Mathf.Abs(source.m33 - 1f) > affineTolerance)
                    throw new InvalidDataException($"Instanced mesh '{record.id}' matrix {index} is not affine.");
                localMatrices[index] = ConvertMatrix(source, unitScale);
            }

            var sourceColors = record.colors ?? Array.Empty<float>();
            var expectedColorValueCount = (long)record.count * 4;
            if (sourceColors.Length != 0 && sourceColors.LongLength != expectedColorValueCount)
                throw new InvalidDataException($"Instanced mesh '{record.id}' has {sourceColors.LongLength} color values for {record.count} instances; expected zero or {expectedColorValueCount}.");
            for (var index = 0; index < sourceColors.Length; index++)
            {
                if (!IsFinite(sourceColors[index]))
                    throw new InvalidDataException($"Instanced mesh '{record.id}' colors contain a non-finite value at index {index}.");
            }
            var instanceColors = sourceColors.Length == 0 ? Array.Empty<Color>() : ReadColors(sourceColors);
            var sharedMaterials = ResolveInstancedMaterials(
                context,
                record,
                meshRecord,
                materials,
                materialRecords,
                instancedColorMaterials,
                instanceColors.Length > 0);

            if (generateColliders)
                context.LogImportWarning($"Instanced node '{node.id}' does not generate per-instance colliders. Re-export with instancedMeshMode: \"expanded\" to use generated colliders.");
            gameObject.AddComponent<ThreeUnityInstancedRenderer>().Initialize(mesh, sharedMaterials, localMatrices, instanceColors);
        }

        private static Material[] ResolveInstancedMaterials(
            AssetImportContext context,
            InstancedMeshRecord instancedRecord,
            MeshRecord meshRecord,
            Dictionary<string, Material> materials,
            MaterialRecord[] materialRecords,
            Dictionary<string, Material> instancedColorMaterials,
            bool useInstanceColors)
        {
            var materialIds = meshRecord.materialIds ?? Array.Empty<string>();
            if (materialIds.Length == 0)
                throw new InvalidDataException($"Instanced mesh '{instancedRecord.id}' mesh '{meshRecord.id}' has no materials.");
            var groups = meshRecord.groups ?? Array.Empty<MeshGroupRecord>();
            var shared = new Material[Math.Max(1, groups.Length)];
            for (var slot = 0; slot < shared.Length; slot++)
            {
                var materialIndex = groups.Length > 0 ? groups[slot].materialIndex : 0;
                if (materialIndex < 0 || materialIndex >= materialIds.Length)
                    throw new InvalidDataException($"Instanced mesh '{instancedRecord.id}' renderer slot {slot} references source material index {materialIndex}, but mesh '{meshRecord.id}' has {materialIds.Length} materials.");
                var materialId = materialIds[materialIndex];
                if (!materials.TryGetValue(materialId, out var material) || material == null)
                    throw new InvalidDataException($"Instanced mesh '{instancedRecord.id}' references missing material '{materialId}'.");
                material.enableInstancing = true;
                shared[slot] = useInstanceColors
                    ? GetOrCreateInstancedColorMaterial(context, materialId, material, materialRecords, instancedColorMaterials)
                    : material;
            }
            return shared;
        }

        private static Material GetOrCreateInstancedColorMaterial(
            AssetImportContext context,
            string materialId,
            Material baseMaterial,
            MaterialRecord[] materialRecords,
            Dictionary<string, Material> instancedColorMaterials)
        {
            if (instancedColorMaterials.TryGetValue(materialId, out var existing)) return existing;
            var shader = Shader.Find("ThreeUnity/Instanced Surface");
            if (shader == null)
                throw new InvalidDataException($"Material '{materialId}' requires per-instance color, but shader 'ThreeUnity/Instanced Surface' was not found.");

            MaterialRecord materialRecord = null;
            foreach (var candidate in materialRecords)
            {
                if (candidate.id == materialId)
                {
                    materialRecord = candidate;
                    break;
                }
            }
            if (materialRecord == null)
                throw new InvalidDataException($"Material record '{materialId}' was not found while creating its instanced-color variant.");

            var baseColor = baseMaterial.HasProperty("_BaseColor")
                ? baseMaterial.GetColor("_BaseColor")
                : baseMaterial.HasProperty("_Color") ? baseMaterial.GetColor("_Color") : Color.white;
            var baseTextureProperty = baseMaterial.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            var baseTexture = baseMaterial.HasProperty(baseTextureProperty) ? baseMaterial.GetTexture(baseTextureProperty) : null;
            var baseTextureScale = baseMaterial.HasProperty(baseTextureProperty) ? baseMaterial.GetTextureScale(baseTextureProperty) : Vector2.one;
            var baseTextureOffset = baseMaterial.HasProperty(baseTextureProperty) ? baseMaterial.GetTextureOffset(baseTextureProperty) : Vector2.zero;

            var variant = new Material(baseMaterial)
            {
                name = $"{baseMaterial.name} Instanced Color",
                shader = shader,
                enableInstancing = true,
            };
            variant.renderQueue = baseMaterial.renderQueue;
            SetColor(variant, "_BaseColor", "_Color", baseColor);
            if (baseTexture != null)
            {
                if (variant.HasProperty("_BaseMap")) variant.SetTexture("_BaseMap", baseTexture);
                if (variant.HasProperty("_MainTex")) variant.SetTexture("_MainTex", baseTexture);
            }
            if (variant.HasProperty("_BaseMap"))
            {
                variant.SetTextureScale("_BaseMap", baseTextureScale);
                variant.SetTextureOffset("_BaseMap", baseTextureOffset);
            }
            if (variant.HasProperty("_MainTex"))
            {
                variant.SetTextureScale("_MainTex", baseTextureScale);
                variant.SetTextureOffset("_MainTex", baseTextureOffset);
            }
            SetFloat(variant, "_UseVertexColor", materialRecord.vertexColors ? 1f : 0f);
            SetFloat(variant, "_Unlit", materialRecord.unlit ? 1f : 0f);
            SetFloat(variant, "_Metallic", materialRecord.metallic);
            SetFloat(variant, "_Smoothness", 1f - Mathf.Clamp01(materialRecord.roughness));
            SetFloat(variant, "_Glossiness", 1f - Mathf.Clamp01(materialRecord.roughness));
            SetFloat(variant, "_Cutoff", materialRecord.alphaCutoff);
            if (variant.HasProperty("_EmissionColor"))
                variant.SetColor("_EmissionColor", ReadColor(materialRecord.emissive, Color.black));
            ConfigureSurface(variant, materialRecord.transparent || baseMaterial.renderQueue >= (int)RenderQueue.Transparent, materialRecord.doubleSided);
            context.AddObjectToAsset($"instanced_color_{materialId}", variant);
            instancedColorMaterials.Add(materialId, variant);
            return variant;
        }

        private static void AttachSkinnedMesh(
            AssetImportContext context,
            GameObject gameObject,
            NodeRecord node,
            Mesh sourceMesh,
            MeshRecord meshRecord,
            SkinRecord skin,
            Dictionary<string, Material> materials,
            Dictionary<string, GameObject> objects,
            float unitScale)
        {
            if (skin.meshNodeId != node.id) throw new InvalidDataException($"Skin '{skin.id}' belongs to node '{skin.meshNodeId}', not '{node.id}'.");
            var boneNodeIds = skin.boneNodeIds ?? Array.Empty<string>();
            if (skin.inverseBindMatrices == null || skin.inverseBindMatrices.Length != boneNodeIds.Length * 16)
                throw new InvalidDataException($"Skin '{skin.id}' must provide one inverse bind matrix for each bone.");
            if (skin.bindMatrix == null || skin.bindMatrix.Length != 16)
                throw new InvalidDataException($"Skin '{skin.id}' must provide a 4x4 bind matrix.");

            var bones = new Transform[boneNodeIds.Length];
            for (var index = 0; index < bones.Length; index++)
            {
                if (!objects.TryGetValue(boneNodeIds[index], out var boneObject))
                    throw new InvalidDataException($"Skin '{skin.id}' references missing bone node '{boneNodeIds[index]}'.");
                bones[index] = boneObject.transform;
            }
            if (!objects.TryGetValue(skin.rootBoneNodeId, out var rootBoneObject))
                throw new InvalidDataException($"Skin '{skin.id}' references missing root bone node '{skin.rootBoneNodeId}'.");

            var bindMatrix = ReadThreeMatrix(skin.bindMatrix, 0);
            var bindposes = new Matrix4x4[bones.Length];
            for (var index = 0; index < bindposes.Length; index++)
            {
                var inverseBindMatrix = ReadThreeMatrix(skin.inverseBindMatrices, index * 16);
                bindposes[index] = ConvertMatrix(inverseBindMatrix * bindMatrix, unitScale);
            }

            var mesh = UnityEngine.Object.Instantiate(sourceMesh);
            mesh.name = string.IsNullOrEmpty(skin.name) ? $"{sourceMesh.name} Skin" : skin.name;
            mesh.bindposes = bindposes;
            context.AddObjectToAsset($"skinned_{skin.id}", mesh);

            var renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = rootBoneObject.transform;
            renderer.localBounds = mesh.bounds;
            AssignMaterials(renderer, meshRecord, materials);
            ApplyMorphWeights(renderer, node);
        }

        private static void AttachMorphMesh(
            GameObject gameObject,
            NodeRecord node,
            Mesh mesh,
            MeshRecord meshRecord,
            Dictionary<string, Material> materials)
        {
            var renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.localBounds = mesh.bounds;
            AssignMaterials(renderer, meshRecord, materials);
            ApplyMorphWeights(renderer, node);
        }

        private static void ApplyMorphWeights(SkinnedMeshRenderer renderer, NodeRecord node)
        {
            var weights = node.morphWeights ?? Array.Empty<float>();
            for (var index = 0; index < weights.Length; index++)
                renderer.SetBlendShapeWeight(index, weights[index] * 100f);
        }

        private static void AssignMaterials(Renderer renderer, MeshRecord record, Dictionary<string, Material> materials)
        {
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
        }

        private static void AssignPrimitiveMaterials(Renderer renderer, PrimitiveRecord record, Dictionary<string, Material> materials)
        {
            var materialIds = record.materialIds ?? Array.Empty<string>();
            if (materialIds.Length == 0) return;
            var groups = record.groups ?? Array.Empty<MeshGroupRecord>();
            var shared = new Material[Math.Max(1, groups.Length)];
            for (var slot = 0; slot < shared.Length; slot++)
            {
                var materialIndex = groups.Length > 0 ? groups[slot].materialIndex : 0;
                if (materialIndex < 0 || materialIndex >= materialIds.Length)
                    throw new InvalidDataException($"Primitive '{record.id}' renderer slot {slot} references source material index {materialIndex}, but has {materialIds.Length} materials.");
                if (!materials.TryGetValue(materialIds[materialIndex], out shared[slot]))
                    throw new InvalidDataException($"Primitive '{record.id}' references missing material '{materialIds[materialIndex]}'.");
            }
            renderer.sharedMaterials = shared;
        }

        private static Dictionary<string, AnimationClip> ImportAnimations(
            AssetImportContext context,
            GameObject root,
            Dictionary<string, GameObject> objects,
            AnimationRecord[] records,
            NodeRecord[] nodeRecords,
            MeshRecord[] meshRecords,
            PrimitiveRecord[] primitiveRecords,
            MaterialRecord[] materialRecords,
            float unitScale,
            out ThreeUnityMaterialAnimationClip[] materialAnimationClips)
        {
            var result = new Dictionary<string, AnimationClip>();
            var usedNames = new HashSet<string>();
            var importedMaterialClips = new List<ThreeUnityMaterialAnimationClip>();
            var nodesById = new Dictionary<string, NodeRecord>();
            foreach (var node in nodeRecords) nodesById.Add(node.id, node);
            var meshesById = new Dictionary<string, MeshRecord>();
            foreach (var mesh in meshRecords) meshesById.Add(mesh.id, mesh);
            var primitivesById = new Dictionary<string, PrimitiveRecord>();
            foreach (var primitive in primitiveRecords) primitivesById.Add(primitive.id, primitive);
            var materialsById = new Dictionary<string, MaterialRecord>();
            foreach (var material in materialRecords) materialsById.Add(material.id, material);

            foreach (var record in records)
            {
                var clipName = string.IsNullOrEmpty(record.name) ? record.id : record.name;
                if (!usedNames.Add(clipName))
                {
                    clipName = $"{clipName} [{record.id}]";
                    usedNames.Add(clipName);
                }
                var clip = new AnimationClip
                {
                    name = clipName,
                    frameRate = 30f,
                    legacy = true,
                    wrapMode = record.loop ? WrapMode.Loop : WrapMode.Once,
                };
                var materialBindings = new List<ThreeUnityMaterialAnimationBinding>();
                foreach (var track in record.tracks ?? Array.Empty<AnimationTrackRecord>())
                {
                    if (!objects.TryGetValue(track.targetNodeId, out var target))
                        throw new InvalidDataException($"Animation '{record.id}' references missing target node '{track.targetNodeId}'.");
                    if (IsMaterialAnimationProperty(track.property))
                    {
                        if (nodesById.TryGetValue(track.targetNodeId, out var materialNode) && !string.IsNullOrEmpty(materialNode.instancedMeshId))
                        {
                            context.LogImportWarning($"Animation '{record.id}' material track for native instanced node '{track.targetNodeId}', material index {track.materialIndex}, property '{track.property}' was skipped. Native instanced material animation is not supported; export with instancedMeshMode: \"expanded\" to retain it.");
                            continue;
                        }
                        AddMaterialAnimationBindings(
                            materialBindings,
                            target,
                            track,
                            record,
                            nodesById,
                            meshesById,
                            primitivesById,
                            materialsById);
                        continue;
                    }
                    var path = AnimationUtility.CalculateTransformPath(target.transform, root.transform);
                    if (track.property == "morphWeight")
                        AttachMorphAnimationTrack(clip, path, target, track, record);
                    else
                        AttachAnimationTrack(clip, path, track, record.duration, record.loop, unitScale);
                }
                if (materialBindings.Count > 0)
                {
                    EnsureMaterialClockDuration(clip, record.duration, record.loop);
                    importedMaterialClips.Add(new ThreeUnityMaterialAnimationClip
                    {
                        clipName = clipName,
                        bindings = materialBindings.ToArray(),
                    });
                }
                clip.EnsureQuaternionContinuity();
                context.AddObjectToAsset(record.id, clip);
                result.Add(record.id, clip);
            }
            materialAnimationClips = importedMaterialClips.ToArray();
            return result;
        }

        private static void AddMaterialAnimationBindings(
            List<ThreeUnityMaterialAnimationBinding> bindings,
            GameObject target,
            AnimationTrackRecord track,
            AnimationRecord animation,
            Dictionary<string, NodeRecord> nodesById,
            Dictionary<string, MeshRecord> meshesById,
            Dictionary<string, PrimitiveRecord> primitivesById,
            Dictionary<string, MaterialRecord> materialsById)
        {
            if (track.interpolation != "linear" || !track.baked)
                throw new InvalidDataException($"Animation '{animation.id}' material track for node '{track.targetNodeId}' must be baked with linear interpolation.");
            if (track.morphTargetIndex != -1)
                throw new InvalidDataException($"Animation '{animation.id}' material track for node '{track.targetNodeId}' must use morphTargetIndex -1.");
            if (!nodesById.TryGetValue(track.targetNodeId, out var node))
                throw new InvalidDataException($"Animation '{animation.id}' material track targets missing node '{track.targetNodeId}'.");
            string renderableKind;
            string renderableId;
            string[] materialIds;
            MeshGroupRecord[] groups;
            if (!string.IsNullOrEmpty(node.meshId) && meshesById.TryGetValue(node.meshId, out var mesh))
            {
                renderableKind = "mesh";
                renderableId = mesh.id;
                materialIds = mesh.materialIds ?? Array.Empty<string>();
                groups = mesh.groups ?? Array.Empty<MeshGroupRecord>();
            }
            else if (!string.IsNullOrEmpty(node.primitiveId) && primitivesById.TryGetValue(node.primitiveId, out var primitive))
            {
                renderableKind = "primitive";
                renderableId = primitive.id;
                materialIds = primitive.materialIds ?? Array.Empty<string>();
                groups = primitive.groups ?? Array.Empty<MeshGroupRecord>();
            }
            else
            {
                throw new InvalidDataException($"Animation '{animation.id}' material track targets node '{track.targetNodeId}' without a mesh or primitive.");
            }
            if (track.materialIndex < 0 || track.materialIndex >= materialIds.Length)
                throw new InvalidDataException($"Animation '{animation.id}' material track targets node '{node.id}', {renderableKind} '{renderableId}', source material index {track.materialIndex}, but it has {materialIds.Length} source materials.");
            var materialId = materialIds[track.materialIndex];
            if (!materialsById.TryGetValue(materialId, out var material))
                throw new InvalidDataException($"Animation '{animation.id}' material track targets node '{node.id}', {renderableKind} '{renderableId}', source material index {track.materialIndex}, whose material '{materialId}' does not exist.");
            if (track.property == "materialBaseMapST" && string.IsNullOrEmpty(material.baseColorTextureId))
                throw new InvalidDataException($"Animation '{animation.id}' base-map ST track targets material '{materialId}' without a base color texture.");

            var property = ReadMaterialAnimationProperty(track.property);
            var componentCount = GetMaterialAnimationComponentCount(property);
            var times = track.times ?? Array.Empty<float>();
            var values = track.values ?? Array.Empty<float>();
            if (times.Length == 0 || values.Length != times.Length * componentCount)
                throw new InvalidDataException($"Animation '{animation.id}' material track '{track.property}' for node '{node.id}' has {values.Length} values for {times.Length} keys; expected {componentCount} values per key.");
            for (var index = 0; index < times.Length; index++)
            {
                if (float.IsNaN(times[index]) || float.IsInfinity(times[index]) || (index > 0 && times[index] < times[index - 1]))
                    throw new InvalidDataException($"Animation '{animation.id}' material track '{track.property}' for node '{node.id}' has invalid key time at index {index}.");
            }
            for (var index = 0; index < values.Length; index++)
            {
                if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                    throw new InvalidDataException($"Animation '{animation.id}' material track '{track.property}' for node '{node.id}' has a non-finite value at index {index}.");
            }

            var renderer = target.GetComponent<Renderer>();
            if (renderer == null)
                throw new InvalidDataException($"Animation '{animation.id}' material track targets node '{node.id}', {renderableKind} '{renderableId}', source material index {track.materialIndex}, but the node has no Renderer.");
            var actualSlotCount = renderer.sharedMaterials.Length;
            var matchedSlots = new List<int>();
            if (groups.Length == 0)
            {
                if (track.materialIndex == 0 && actualSlotCount > 0) matchedSlots.Add(0);
            }
            else
            {
                for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                {
                    if (groups[groupIndex].materialIndex == track.materialIndex && groupIndex < actualSlotCount)
                        matchedSlots.Add(groupIndex);
                }
            }
            if (matchedSlots.Count == 0)
                throw new InvalidDataException($"Animation '{animation.id}' material track targets node '{node.id}', {renderableKind} '{renderableId}', source material index {track.materialIndex}, which maps to no renderer slot among {actualSlotCount} slots.");

            var initialValue = ReadInitialMaterialValue(material, property);
            foreach (var materialSlot in matchedSlots)
            {
                bindings.Add(new ThreeUnityMaterialAnimationBinding
                {
                    renderer = renderer,
                    materialSlot = materialSlot,
                    property = property,
                    times = times,
                    values = values,
                    initialValue = initialValue,
                });
            }
        }

        private static void EnsureMaterialClockDuration(AnimationClip clip, float duration, bool loop)
        {
            if (duration <= 0f || clip.length >= duration) return;
            var curve = AnimationCurve.Linear(0f, 0f, duration, 0f);
            curve.preWrapMode = loop ? WrapMode.Loop : WrapMode.Once;
            curve.postWrapMode = loop ? WrapMode.Loop : WrapMode.Once;
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(ThreeUnityAnimationPlayer), "materialAnimationClock");
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void AttachAnimationTrack(AnimationClip clip, string path, AnimationTrackRecord track, float duration, bool loop, float unitScale)
        {
            if (track.interpolation != "linear")
                throw new InvalidDataException($"Animation track for node '{track.targetNodeId}' uses unsupported interpolation '{track.interpolation}'.");
            var componentCount = track.property == "quaternion" ? 4 : track.property == "position" || track.property == "scale" ? 3 : 0;
            if (componentCount == 0) throw new InvalidDataException($"Animation track for node '{track.targetNodeId}' uses unsupported property '{track.property}'.");
            var times = track.times ?? Array.Empty<float>();
            var values = track.values ?? Array.Empty<float>();
            if (values.Length != times.Length * componentCount)
                throw new InvalidDataException($"Animation track for node '{track.targetNodeId}' has {values.Length} values for {times.Length} keys.");

            var curves = new AnimationCurve[componentCount];
            for (var component = 0; component < componentCount; component++) curves[component] = new AnimationCurve();
            var previousQuaternion = Quaternion.identity;
            for (var keyIndex = 0; keyIndex < times.Length; keyIndex++)
            {
                var offset = keyIndex * componentCount;
                if (track.property == "position")
                {
                    curves[0].AddKey(times[keyIndex], values[offset] * unitScale);
                    curves[1].AddKey(times[keyIndex], values[offset + 1] * unitScale);
                    curves[2].AddKey(times[keyIndex], -values[offset + 2] * unitScale);
                }
                else if (track.property == "scale")
                {
                    for (var component = 0; component < 3; component++) curves[component].AddKey(times[keyIndex], values[offset + component]);
                }
                else
                {
                    var quaternion = new Quaternion(-values[offset], -values[offset + 1], values[offset + 2], values[offset + 3]);
                    if (keyIndex > 0 && Quaternion.Dot(previousQuaternion, quaternion) < 0f)
                        quaternion = new Quaternion(-quaternion.x, -quaternion.y, -quaternion.z, -quaternion.w);
                    previousQuaternion = quaternion;
                    curves[0].AddKey(times[keyIndex], quaternion.x);
                    curves[1].AddKey(times[keyIndex], quaternion.y);
                    curves[2].AddKey(times[keyIndex], quaternion.z);
                    curves[3].AddKey(times[keyIndex], quaternion.w);
                }
            }

            if (times.Length > 0 && times[times.Length - 1] < duration)
            {
                for (var component = 0; component < componentCount; component++)
                    curves[component].AddKey(duration, curves[component].keys[curves[component].length - 1].value);
            }
            foreach (var curve in curves)
            {
                curve.preWrapMode = loop ? WrapMode.Loop : WrapMode.Once;
                curve.postWrapMode = loop ? WrapMode.Loop : WrapMode.Once;
                for (var index = 0; index < curve.length; index++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.Linear);
                }
            }

            var propertyPrefix = track.property == "position" ? "m_LocalPosition" : track.property == "quaternion" ? "m_LocalRotation" : "m_LocalScale";
            var suffixes = componentCount == 4 ? new[] { ".x", ".y", ".z", ".w" } : new[] { ".x", ".y", ".z" };
            for (var component = 0; component < componentCount; component++)
            {
                var binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), propertyPrefix + suffixes[component]);
                AnimationUtility.SetEditorCurve(clip, binding, curves[component]);
            }
        }

        private static void AttachMorphAnimationTrack(
            AnimationClip clip,
            string path,
            GameObject target,
            AnimationTrackRecord track,
            AnimationRecord animation)
        {
            if (track.interpolation != "linear")
                throw new InvalidDataException($"Animation '{animation.id}' morph track for node '{track.targetNodeId}' uses unsupported interpolation '{track.interpolation}'.");

            var renderer = target.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null || renderer.sharedMesh == null)
                throw new InvalidDataException($"Animation '{animation.id}' morph track targets node '{track.targetNodeId}' without a SkinnedMeshRenderer.");
            if (track.morphTargetIndex < 0 || track.morphTargetIndex >= renderer.sharedMesh.blendShapeCount)
                throw new InvalidDataException($"Animation '{animation.id}' morph track targets index {track.morphTargetIndex} on node '{track.targetNodeId}', which has {renderer.sharedMesh.blendShapeCount} morph targets.");

            var times = track.times ?? Array.Empty<float>();
            var values = track.values ?? Array.Empty<float>();
            if (values.Length != times.Length)
                throw new InvalidDataException($"Animation '{animation.id}' morph track for node '{track.targetNodeId}' has {values.Length} values for {times.Length} keys.");

            var curve = new AnimationCurve();
            for (var keyIndex = 0; keyIndex < times.Length; keyIndex++)
                curve.AddKey(times[keyIndex], values[keyIndex] * 100f);
            if (times.Length > 0 && times[times.Length - 1] < animation.duration)
                curve.AddKey(animation.duration, curve.keys[curve.length - 1].value);
            curve.preWrapMode = animation.loop ? WrapMode.Loop : WrapMode.Once;
            curve.postWrapMode = animation.loop ? WrapMode.Loop : WrapMode.Once;
            for (var index = 0; index < curve.length; index++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.Linear);
            }

            var targetName = renderer.sharedMesh.GetBlendShapeName(track.morphTargetIndex);
            var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), $"blendShape.{targetName}");
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void AttachAnimationPlayer(
            GameObject root,
            Dictionary<string, AnimationClip> clipsById,
            AnimationRecord[] records,
            string defaultAnimationId,
            bool autoplay,
            ThreeUnityMaterialAnimationClip[] materialAnimationClips)
        {
            if (clipsById.Count == 0) return;
            var clips = new AnimationClip[records.Length];
            for (var index = 0; index < records.Length; index++) clips[index] = clipsById[records[index].id];
            var defaultClipName = string.Empty;
            var defaultLoop = false;
            AnimationClip defaultClip = null;
            if (!string.IsNullOrEmpty(defaultAnimationId))
            {
                if (!clipsById.TryGetValue(defaultAnimationId, out defaultClip))
                    throw new InvalidDataException($"Default animation '{defaultAnimationId}' does not exist.");
                defaultClipName = defaultClip.name;
                foreach (var record in records) if (record.id == defaultAnimationId) { defaultLoop = record.loop; break; }
            }
            root.AddComponent<ThreeUnityAnimationPlayer>().Initialize(clips, defaultClipName, autoplay, defaultLoop, materialAnimationClips);
            var animationComponent = root.GetComponent<Animation>();
            animationComponent.playAutomatically = false;
            animationComponent.clip = defaultClip;
        }

        private static void ApplyAnimatedBounds(GameObject root, IEnumerable<AnimationClip> clips)
        {
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var clipList = new List<AnimationClip>(clips);
            if (renderers.Length == 0) return;

            var transforms = root.GetComponentsInChildren<Transform>(true);
            var positions = new Vector3[transforms.Length];
            var rotations = new Quaternion[transforms.Length];
            var scales = new Vector3[transforms.Length];
            for (var index = 0; index < transforms.Length; index++)
            {
                positions[index] = transforms[index].localPosition;
                rotations[index] = transforms[index].localRotation;
                scales[index] = transforms[index].localScale;
            }
            var blendShapeWeights = CaptureBlendShapeWeights(renderers);

            var bakedMesh = new Mesh { name = "ThreeUnity Animated Bounds Probe" };
            try
            {
                foreach (var renderer in renderers)
                {
                    RestoreLocalTransforms(transforms, positions, rotations, scales);
                    RestoreBlendShapeWeights(renderers, blendShapeWeights);
                    var animatedBounds = renderer.sharedMesh.bounds;
                    bakedMesh.Clear();
                    renderer.BakeMesh(bakedMesh);
                    animatedBounds.Encapsulate(bakedMesh.bounds.min);
                    animatedBounds.Encapsulate(bakedMesh.bounds.max);
                    foreach (var clip in clipList)
                    {
                        RestoreLocalTransforms(transforms, positions, rotations, scales);
                        RestoreBlendShapeWeights(renderers, blendShapeWeights);
                        foreach (var sampleTime in CollectAnimationSampleTimes(clip))
                        {
                            clip.SampleAnimation(root, sampleTime);
                            bakedMesh.Clear();
                            renderer.BakeMesh(bakedMesh);
                            animatedBounds.Encapsulate(bakedMesh.bounds.min);
                            animatedBounds.Encapsulate(bakedMesh.bounds.max);
                        }
                    }
                    var margin = Mathf.Max(0.001f, animatedBounds.size.magnitude * 0.01f);
                    animatedBounds.Expand(margin * 2f);
                    renderer.localBounds = animatedBounds;
                }
            }
            finally
            {
                RestoreLocalTransforms(transforms, positions, rotations, scales);
                RestoreBlendShapeWeights(renderers, blendShapeWeights);
                UnityEngine.Object.DestroyImmediate(bakedMesh);
            }
        }

        private static float[][] CaptureBlendShapeWeights(SkinnedMeshRenderer[] renderers)
        {
            var weights = new float[renderers.Length][];
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                weights[rendererIndex] = new float[renderer.sharedMesh.blendShapeCount];
                for (var blendShapeIndex = 0; blendShapeIndex < weights[rendererIndex].Length; blendShapeIndex++)
                    weights[rendererIndex][blendShapeIndex] = renderer.GetBlendShapeWeight(blendShapeIndex);
            }
            return weights;
        }

        private static void RestoreBlendShapeWeights(SkinnedMeshRenderer[] renderers, float[][] weights)
        {
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            for (var blendShapeIndex = 0; blendShapeIndex < weights[rendererIndex].Length; blendShapeIndex++)
                renderers[rendererIndex].SetBlendShapeWeight(blendShapeIndex, weights[rendererIndex][blendShapeIndex]);
        }

        private static SortedSet<float> CollectAnimationSampleTimes(AnimationClip clip)
        {
            var sampleTimes = new SortedSet<float> { 0f, clip.length };
            var sampleRate = Mathf.Max(60f, clip.frameRate * 2f);
            var sampleSegments = Mathf.Max(1, Mathf.CeilToInt(clip.length * sampleRate));
            for (var sampleIndex = 0; sampleIndex <= sampleSegments; sampleIndex++)
                sampleTimes.Add(clip.length * sampleIndex / sampleSegments);

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                foreach (var key in curve.keys) sampleTimes.Add(key.time);
            }

            var orderedTimes = new List<float>(sampleTimes);
            for (var index = 1; index < orderedTimes.Count; index++)
                sampleTimes.Add((orderedTimes[index - 1] + orderedTimes[index]) * 0.5f);
            return sampleTimes;
        }

        private static void RestoreLocalTransforms(Transform[] transforms, Vector3[] positions, Quaternion[] rotations, Vector3[] scales)
        {
            for (var index = 0; index < transforms.Length; index++)
            {
                transforms[index].localPosition = positions[index];
                transforms[index].localRotation = rotations[index];
                transforms[index].localScale = scales[index];
            }
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

        private static Shader FindShader(string renderMode, bool unlit, bool vertexColors)
        {
            if (renderMode == "line") return Shader.Find("ThreeUnity/Unlit Line");
            if (renderMode == "points" || renderMode == "sprite") return Shader.Find("ThreeUnity/Billboard");
            if (renderMode != "surface") return null;
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
            if (fallback != preferred && material.HasProperty(fallback)) material.SetColor(fallback, value);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetTexture(Material material, string preferred, string fallback, string id, Dictionary<string, Texture2D> textures)
        {
            if (string.IsNullOrEmpty(id) || !textures.TryGetValue(id, out var texture)) return;
            if (material.HasProperty(preferred)) material.SetTexture(preferred, texture);
            if (fallback != preferred && material.HasProperty(fallback)) material.SetTexture(fallback, texture);
        }

        private static TextureWrapMode ReadTextureWrapMode(string value, string textureName)
        {
            switch (value)
            {
                case "repeat": return TextureWrapMode.Repeat;
                case "clamp": return TextureWrapMode.Clamp;
                case "mirror": return TextureWrapMode.Mirror;
                default: throw new InvalidDataException($"Texture '{textureName}' uses unsupported wrap mode '{value}'.");
            }
        }

        private static Vector4 ReadBaseMapST(float[] values)
        {
            if (values == null || values.Length == 0) return new Vector4(1f, 1f, 0f, 0f);
            if (values.Length != 4)
                throw new InvalidDataException($"Base color texture ST must contain exactly four values, but found {values.Length}.");
            for (var index = 0; index < values.Length; index++)
            {
                if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                    throw new InvalidDataException($"Base color texture ST contains a non-finite value at index {index}.");
            }
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        private static void ApplyBaseMapST(Material material, Vector4 st)
        {
            st = ThreeUnityMaterialAnimationUtility.ConvertBaseMapST(st);
            var scale = new Vector2(st.x, st.y);
            var offset = new Vector2(st.z, st.w);
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTextureScale("_BaseMap", scale);
                material.SetTextureOffset("_BaseMap", offset);
            }
            if (material.HasProperty("_MainTex"))
            {
                material.SetTextureScale("_MainTex", scale);
                material.SetTextureOffset("_MainTex", offset);
            }
            if (material.HasProperty("_BaseMap_ST")) material.SetVector("_BaseMap_ST", st);
            if (material.HasProperty("_MainTex_ST")) material.SetVector("_MainTex_ST", st);
        }

        private static bool IsMaterialAnimationProperty(string property)
        {
            return property == "materialBaseColor" ||
                   property == "materialEmissive" ||
                   property == "materialMetallic" ||
                   property == "materialRoughness" ||
                   property == "materialBaseMapST";
        }

        private static ThreeUnityMaterialAnimationProperty ReadMaterialAnimationProperty(string property)
        {
            switch (property)
            {
                case "materialBaseColor": return ThreeUnityMaterialAnimationProperty.BaseColor;
                case "materialEmissive": return ThreeUnityMaterialAnimationProperty.Emissive;
                case "materialMetallic": return ThreeUnityMaterialAnimationProperty.Metallic;
                case "materialRoughness": return ThreeUnityMaterialAnimationProperty.Roughness;
                case "materialBaseMapST": return ThreeUnityMaterialAnimationProperty.BaseMapST;
                default: throw new InvalidDataException($"Unsupported material animation property '{property}'.");
            }
        }

        private static int GetMaterialAnimationComponentCount(ThreeUnityMaterialAnimationProperty property)
        {
            switch (property)
            {
                case ThreeUnityMaterialAnimationProperty.BaseColor:
                case ThreeUnityMaterialAnimationProperty.BaseMapST:
                    return 4;
                case ThreeUnityMaterialAnimationProperty.Emissive:
                    return 3;
                default:
                    return 1;
            }
        }

        private static Vector4 ReadInitialMaterialValue(MaterialRecord material, ThreeUnityMaterialAnimationProperty property)
        {
            switch (property)
            {
                case ThreeUnityMaterialAnimationProperty.BaseColor:
                    var baseColor = ReadColor(material.baseColor, Color.white);
                    return new Vector4(baseColor.r, baseColor.g, baseColor.b, baseColor.a);
                case ThreeUnityMaterialAnimationProperty.Emissive:
                    var emissive = ReadColor(material.emissive, Color.black);
                    return new Vector4(emissive.r, emissive.g, emissive.b, 0f);
                case ThreeUnityMaterialAnimationProperty.Metallic:
                    return new Vector4(material.metallic, 0f, 0f, 0f);
                case ThreeUnityMaterialAnimationProperty.Roughness:
                    return new Vector4(material.roughness, 0f, 0f, 0f);
                case ThreeUnityMaterialAnimationProperty.BaseMapST:
                    return ReadBaseMapST(material.baseColorTextureST);
                default:
                    throw new InvalidDataException($"Unsupported material animation property '{property}'.");
            }
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

        private static BoneWeight[] ReadBoneWeights(int[] indices, float[] weights, int vertexCount)
        {
            var output = new BoneWeight[vertexCount];
            for (var index = 0; index < vertexCount; index++)
            {
                var offset = index * 4;
                output[index] = new BoneWeight
                {
                    boneIndex0 = indices[offset],
                    boneIndex1 = indices[offset + 1],
                    boneIndex2 = indices[offset + 2],
                    boneIndex3 = indices[offset + 3],
                    weight0 = weights[offset],
                    weight1 = weights[offset + 1],
                    weight2 = weights[offset + 2],
                    weight3 = weights[offset + 3],
                };
            }
            return output;
        }

        private static Matrix4x4 ReadThreeMatrix(float[] values, int offset)
        {
            var matrix = new Matrix4x4();
            for (var column = 0; column < 4; column++)
            for (var row = 0; row < 4; row++)
                matrix[row, column] = values[offset + column * 4 + row];
            return matrix;
        }

        private static Matrix4x4 ConvertMatrix(Matrix4x4 source, float unitScale)
        {
            var mirror = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            var converted = mirror * source * mirror;
            converted.m03 *= unitScale;
            converted.m13 *= unitScale;
            converted.m23 *= unitScale;
            return converted;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
            public PrimitiveRecord[] primitives;
            public InstancedMeshRecord[] instancedMeshes;
            public MaterialRecord[] materials;
            public TextureRecord[] textures;
            public SkinRecord[] skins;
            public AnimationRecord[] animations;
            public string defaultAnimationId;
            public bool autoplayAnimation;
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
            public string primitiveId;
            public string instancedMeshId;
            public string skinId;
            public float[] morphWeights;
            public CameraRecord camera;
            public LightRecord light;
            public string metadataJson;
            public ComponentRecord[] components;
        }

        [Serializable] private sealed class CameraRecord { public string type; public float fov; public float near; public float far; public float top; public float bottom; }
        [Serializable] private sealed class LightRecord { public string type; public float[] color; public float intensity; public float range; public float spotAngleRadians; public float penumbra; public bool castShadow; }
        [Serializable] private sealed class ComponentRecord { public string type; public string dataJson; }
        [Serializable] private sealed class MeshRecord { public string id; public string name; public float[] positions; public float[] normals; public float[] uv0; public float[] colors; public int[] indices; public MeshGroupRecord[] groups; public string[] materialIds; public int[] skinIndices; public float[] skinWeights; public MorphTargetRecord[] morphTargets; }
        [Serializable] private sealed class PrimitiveRecord { public string id; public string name; public string type; public float[] positions; public float[] colors; public int[] indices; public MeshGroupRecord[] groups; public string[] materialIds; public float[] spriteCenter; }
        [Serializable] private sealed class InstancedMeshRecord { public string id; public string name; public string meshId; public int count; public float[] matrices; public float[] colors; }
        [Serializable] private sealed class MorphTargetRecord { public string name; public float[] positionDeltas; public float[] normalDeltas; }
        [Serializable] private sealed class MeshGroupRecord { public int start; public int count; public int materialIndex; }
        [Serializable] private sealed class SkinRecord { public string id; public string name; public string meshNodeId; public string[] boneNodeIds; public string rootBoneNodeId; public float[] inverseBindMatrices; public float[] bindMatrix; }
        [Serializable] private sealed class AnimationRecord { public string id; public string name; public float duration; public bool loop; public AnimationTrackRecord[] tracks; }
        [Serializable] private sealed class AnimationTrackRecord { public string targetNodeId; public string property; public int morphTargetIndex = -1; public int materialIndex = -1; public float[] times; public float[] values; public string interpolation; public bool baked; }
        [Serializable] private sealed class MaterialRecord
        {
            public string id; public string name; public float[] baseColor; public float[] emissive; public float metallic; public float roughness = 0.5f; public bool transparent; public bool doubleSided; public float alphaCutoff; public bool unlit; public bool vertexColors;
            public string baseColorTextureId; public string emissiveTextureId; public string normalTextureId; public float[] baseColorTextureST;
            public string renderMode = "surface"; public float pointSize = 1f; public bool sizeAttenuation = true; public float spriteRotation;
        }
        [Serializable] private sealed class TextureRecord
        {
            public string id; public string name; public int width; public int height; public string encoding; public string data;
            public string mimeType; public string pixelFormat; public string componentType; public bool flipY; public string colorSpace;
            public string wrapS; public string wrapT; public string filterMode; public bool mipmaps; public int anisotropy;
        }
        private sealed class MaterialAnimationCapabilities { public bool transparent; public bool emission; }
        private sealed class ImportedPrimitive
        {
            public readonly PrimitiveRecord record;
            public readonly Mesh mesh;
            public ImportedPrimitive(PrimitiveRecord sourceRecord, Mesh importedMesh) { record = sourceRecord; mesh = importedMesh; }
        }
        [Serializable] private sealed class RuntimeRecord
        {
            public string controller = "none"; public string colliderMode = "none"; public bool enableBlockEditing; public bool allowFly; public string hudStyle = "diagnostic";
            public float moveSpeed = 5.5f; public float sprintSpeed = 9f; public float flySpeed = 8f; public HotbarItemRecord[] hotbar;
        }
        [Serializable] private sealed class HotbarItemRecord { public string name; public float[] color; }
    }
}

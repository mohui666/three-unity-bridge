using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace ThreeUnity.Bridge.Editor
{
    [ScriptedImporter(5, "threeunity")]
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

            if (document == null || document.format != "three-unity-scene" || (document.version != 1 && document.version != 2))
            {
                context.LogImportError("Unsupported document. Expected three-unity-scene format version 1 or 2.");
                return;
            }

            var root = new GameObject(string.IsNullOrEmpty(document.name) ? Path.GetFileNameWithoutExtension(context.assetPath) : document.name);
            AttachRuntimeProfile(root, document.runtime);
            var textures = ImportTextures(context, document.textures ?? Array.Empty<TextureRecord>());
            var materials = ImportMaterials(context, document.materials ?? Array.Empty<MaterialRecord>(), textures);
            var meshes = ImportMeshes(context, document.meshes ?? Array.Empty<MeshRecord>(), document.unitScaleMeters);
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
                AttachMesh(context, gameObject, node, meshes, materials, document.meshes, skins, objects, document.unitScaleMeters);
                if (importCameras && node.camera != null && !string.IsNullOrEmpty(node.camera.type))
                    AttachCamera(gameObject, node.camera, document.unitScaleMeters);
                if (importLights && node.light != null && !string.IsNullOrEmpty(node.light.type))
                    AttachLight(gameObject, node.light, document.unitScaleMeters);
            }

            if (document.version >= 2)
            {
                var animationRecords = document.animations ?? Array.Empty<AnimationRecord>();
                var clips = ImportAnimations(context, root, objects, animationRecords, document.unitScaleMeters);
                ApplyAnimatedBounds(root, clips.Values);
                AttachAnimationPlayer(root, clips, animationRecords, document.defaultAnimationId, document.autoplayAnimation);
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

        private static Dictionary<string, SkinRecord> IndexSkins(SkinRecord[] records)
        {
            var result = new Dictionary<string, SkinRecord>();
            foreach (var record in records) result.Add(record.id, record);
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
                mesh.RecalculateBounds();
                context.AddObjectToAsset(record.id, mesh);
                result[record.id] = mesh;
            }
            return result;
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

            if (!string.IsNullOrEmpty(node.skinId))
            {
                if (!skins.TryGetValue(node.skinId, out var skin)) throw new InvalidDataException($"Node '{node.id}' references missing skin '{node.skinId}'.");
                AttachSkinnedMesh(context, gameObject, node, mesh, record, skin, materials, objects, unitScale);
                return;
            }

            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            AssignMaterials(renderer, record, materials);
            if (generateColliders) gameObject.AddComponent<MeshCollider>().sharedMesh = mesh;
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

        private static Dictionary<string, AnimationClip> ImportAnimations(
            AssetImportContext context,
            GameObject root,
            Dictionary<string, GameObject> objects,
            AnimationRecord[] records,
            float unitScale)
        {
            var result = new Dictionary<string, AnimationClip>();
            var usedNames = new HashSet<string>();
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
                foreach (var track in record.tracks ?? Array.Empty<AnimationTrackRecord>())
                {
                    if (!objects.TryGetValue(track.targetNodeId, out var target))
                        throw new InvalidDataException($"Animation '{record.id}' references missing target node '{track.targetNodeId}'.");
                    var path = AnimationUtility.CalculateTransformPath(target.transform, root.transform);
                    AttachAnimationTrack(clip, path, track, record.duration, record.loop, unitScale);
                }
                clip.EnsureQuaternionContinuity();
                context.AddObjectToAsset(record.id, clip);
                result.Add(record.id, clip);
            }
            return result;
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

        private static void AttachAnimationPlayer(
            GameObject root,
            Dictionary<string, AnimationClip> clipsById,
            AnimationRecord[] records,
            string defaultAnimationId,
            bool autoplay)
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
            root.AddComponent<ThreeUnityAnimationPlayer>().Initialize(clips, defaultClipName, autoplay, defaultLoop);
            var animationComponent = root.GetComponent<Animation>();
            animationComponent.playAutomatically = false;
            animationComponent.clip = defaultClip;
        }

        private static void ApplyAnimatedBounds(GameObject root, IEnumerable<AnimationClip> clips)
        {
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var clipList = new List<AnimationClip>(clips);
            if (renderers.Length == 0 || clipList.Count == 0) return;

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

            var bakedMesh = new Mesh { name = "ThreeUnity Animated Bounds Probe" };
            try
            {
                foreach (var renderer in renderers)
                {
                    var animatedBounds = renderer.sharedMesh.bounds;
                    foreach (var clip in clipList)
                    {
                        RestoreLocalTransforms(transforms, positions, rotations, scales);
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
                UnityEngine.Object.DestroyImmediate(bakedMesh);
            }
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
            public string skinId;
            public CameraRecord camera;
            public LightRecord light;
            public string metadataJson;
            public ComponentRecord[] components;
        }

        [Serializable] private sealed class CameraRecord { public string type; public float fov; public float near; public float far; public float top; public float bottom; }
        [Serializable] private sealed class LightRecord { public string type; public float[] color; public float intensity; public float range; public float spotAngleRadians; public float penumbra; public bool castShadow; }
        [Serializable] private sealed class ComponentRecord { public string type; public string dataJson; }
        [Serializable] private sealed class MeshRecord { public string id; public string name; public float[] positions; public float[] normals; public float[] uv0; public float[] colors; public int[] indices; public MeshGroupRecord[] groups; public string[] materialIds; public int[] skinIndices; public float[] skinWeights; }
        [Serializable] private sealed class MeshGroupRecord { public int start; public int count; public int materialIndex; }
        [Serializable] private sealed class SkinRecord { public string id; public string name; public string meshNodeId; public string[] boneNodeIds; public string rootBoneNodeId; public float[] inverseBindMatrices; public float[] bindMatrix; }
        [Serializable] private sealed class AnimationRecord { public string id; public string name; public float duration; public bool loop; public AnimationTrackRecord[] tracks; }
        [Serializable] private sealed class AnimationTrackRecord { public string targetNodeId; public string property; public float[] times; public float[] values; public string interpolation; public bool baked; }
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

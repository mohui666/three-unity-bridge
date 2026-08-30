using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThreeUnity.Bridge.Editor
{
    public static class ThreeUnityPlayableSceneBuilder
    {
        [MenuItem("Assets/Three Unity/Create Playable Scene", true)]
        private static bool CanCreateSelectedScene()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return path.EndsWith(".threeunity", StringComparison.OrdinalIgnoreCase);
        }

        [MenuItem("Assets/Three Unity/Create Playable Scene")]
        private static void CreateSelectedScene()
        {
            var assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "Assets";
            var fileName = Path.GetFileNameWithoutExtension(assetPath);
            var scenePath = $"{directory}/{fileName}Playable.unity";
            CreateScene(assetPath, scenePath);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            Debug.Log($"THREE_UNITY_PLAYABLE_SCENE_CREATED asset={assetPath} scene={scenePath}");
        }

        public static GameObject CreateScene(string assetPath, string scenePath)
        {
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null) throw new InvalidOperationException($"Converted asset is missing: {assetPath}");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var instance = UnityEngine.Object.Instantiate(asset);
            var profile = instance.GetComponent<ThreeUnityRuntimeProfile>();
            if (profile == null) throw new InvalidOperationException($"Converted asset has no runtime profile: {assetPath}");

            AddColliders(instance, profile.colliderMode);
            if (profile.controller == "first-person") CreateFirstPersonPlayer(instance, profile);
            else if (profile.controller == "orbit") CreateOrbitCamera(instance, profile);

            if (!EditorSceneManager.SaveScene(scene, scenePath))
                throw new InvalidOperationException($"Could not save generated scene: {scenePath}");
            return instance;
        }

        public static int AddColliders(GameObject instance, string mode)
        {
            if (string.IsNullOrEmpty(mode) || mode == "none") return 0;
            var count = 0;
            foreach (var filter in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;
                if (mode == "box-per-24-vertex-mesh" && filter.sharedMesh.vertexCount != 24) continue;
                if (mode == "box-per-mesh" || mode == "box-per-24-vertex-mesh")
                {
                    var collider = filter.gameObject.AddComponent<BoxCollider>();
                    collider.center = filter.sharedMesh.bounds.center;
                    collider.size = filter.sharedMesh.bounds.size;
                }
                else if (mode == "mesh")
                {
                    filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                }
                count++;
            }
            return count;
        }

        private static void CreateFirstPersonPlayer(GameObject instance, ThreeUnityRuntimeProfile profile)
        {
            var sourceCamera = DisableImportedCameras(instance);
            var sourcePosition = sourceCamera != null ? sourceCamera.transform.position : FindSafeSpawn(instance);
            var sourceRotation = sourceCamera != null ? sourceCamera.transform.rotation : Quaternion.Euler(12f, 35f, 0f);

            var player = new GameObject("Unity First Person Player");
            player.transform.position = sourcePosition - Vector3.up * 1.62f;
            player.transform.rotation = Quaternion.Euler(0f, sourceRotation.eulerAngles.y, 0f);
            var character = player.AddComponent<CharacterController>();
            character.height = 1.8f;
            character.radius = 0.35f;
            character.center = new Vector3(0f, 0.9f, 0f);
            character.stepOffset = 0.35f;

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(player.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 1.62f, 0f);
            cameraObject.transform.localRotation = Quaternion.Euler(NormalizeAngle(sourceRotation.eulerAngles.x), 0f, 0f);
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = sourceCamera != null && !sourceCamera.orthographic ? sourceCamera.fieldOfView : 75f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 500f;
            cameraObject.AddComponent<AudioListener>();
            player.AddComponent<ThreeUnityFirstPersonController>().Configure(camera, instance.transform, profile);
        }

        private static void CreateOrbitCamera(GameObject instance, ThreeUnityRuntimeProfile profile)
        {
            var sourceCamera = DisableImportedCameras(instance);
            var focus = sourceCamera != null ? CameraGroundFocus(sourceCamera) : FindRendererMedian(instance);
            var distance = sourceCamera != null ? Mathf.Clamp(Vector3.Distance(sourceCamera.transform.position, focus), 12f, 90f) : 40f;
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 48f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 800f;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<ThreeUnityOrbitShowcaseController>().Configure(profile.gameObject.name, focus, distance);
        }

        private static Camera DisableImportedCameras(GameObject instance)
        {
            Camera first = null;
            foreach (var camera in instance.GetComponentsInChildren<Camera>(true))
            {
                first ??= camera;
                camera.enabled = false;
                var listener = camera.GetComponent<AudioListener>();
                if (listener != null) listener.enabled = false;
            }
            return first;
        }

        private static Vector3 FindSafeSpawn(GameObject instance)
        {
            var center = FindRendererMedian(instance);
            var highest = center.y;
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                var bounds = renderer.bounds;
                if (Mathf.Abs(bounds.center.x - center.x) < 3f && Mathf.Abs(bounds.center.z - center.z) < 3f)
                    highest = Mathf.Max(highest, bounds.max.y);
            }
            return new Vector3(center.x, highest + 3f, center.z);
        }

        private static Vector3 FindRendererMedian(GameObject instance)
        {
            var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                xs.Add(renderer.bounds.center.x); ys.Add(renderer.bounds.center.y); zs.Add(renderer.bounds.center.z);
            }
            if (xs.Count == 0) return Vector3.zero;
            xs.Sort(); ys.Sort(); zs.Sort();
            return new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);
        }

        private static Vector3 CameraGroundFocus(Camera camera)
        {
            var plane = new Plane(Vector3.up, Vector3.zero);
            var ray = new Ray(camera.transform.position, camera.transform.forward);
            return plane.Raycast(ray, out var distance) ? ray.GetPoint(distance) : camera.transform.position + camera.transform.forward * 35f;
        }

        private static float NormalizeAngle(float value) => value > 180f ? value - 360f : value;
    }
}

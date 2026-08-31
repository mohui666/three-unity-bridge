using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animation))]
    public sealed class ThreeUnityAnimationPlayer : MonoBehaviour
    {
        [SerializeField] private AnimationClip[] clips = Array.Empty<AnimationClip>();
        [SerializeField] private string defaultClipName = string.Empty;
        [SerializeField] private bool playOnAwake = true;
        [SerializeField] private bool loop = true;
        [SerializeField] private ThreeUnityMaterialAnimationClip[] materialClips = Array.Empty<ThreeUnityMaterialAnimationClip>();
        [SerializeField, HideInInspector] private float materialAnimationClock;

        private Animation animationComponent;
        private MaterialClipRuntime[] materialClipRuntimes = Array.Empty<MaterialClipRuntime>();
        private MaterialClipRuntime activeMaterialClip;
        private Transform[] initialTransforms = Array.Empty<Transform>();
        private Vector3[] initialPositions = Array.Empty<Vector3>();
        private Quaternion[] initialRotations = Array.Empty<Quaternion>();
        private Vector3[] initialScales = Array.Empty<Vector3>();
        private SkinnedMeshRenderer[] initialSkinnedRenderers = Array.Empty<SkinnedMeshRenderer>();
        private float[][] initialBlendShapeWeights = Array.Empty<float[]>();
        private bool initialized;
        private bool isContinuousPlayback;

        public AnimationClip[] Clips => clips;
        public string DefaultClipName => defaultClipName;
        public bool PlayOnAwake => playOnAwake;
        public bool Loop => loop;

        public void Initialize(AnimationClip[] importedClips, string importedDefaultClipName, bool shouldPlayOnAwake, bool shouldLoop)
        {
            Initialize(importedClips, importedDefaultClipName, shouldPlayOnAwake, shouldLoop, Array.Empty<ThreeUnityMaterialAnimationClip>());
        }

        internal void Initialize(
            AnimationClip[] importedClips,
            string importedDefaultClipName,
            bool shouldPlayOnAwake,
            bool shouldLoop,
            ThreeUnityMaterialAnimationClip[] importedMaterialClips)
        {
            clips = importedClips;
            defaultClipName = importedDefaultClipName;
            playOnAwake = shouldPlayOnAwake;
            loop = shouldLoop;
            materialClips = importedMaterialClips;
            initialized = false;
        }

        private void Awake()
        {
            EnsureInitialized();
        }

        private void Start()
        {
            if (playOnAwake && !string.IsNullOrEmpty(defaultClipName))
            {
                Play(defaultClipName, loop);
            }
        }

        public bool Play(string clipName)
        {
            var clip = FindClip(clipName);
            return Play(clipName, clip.wrapMode == WrapMode.Loop);
        }

        public bool Play(string clipName, bool shouldLoop)
        {
            var clip = FindClip(clipName);
            EnsureInitialized();
            SelectMaterialClip(clipName);
            var state = animationComponent[clipName];
            state.wrapMode = shouldLoop ? WrapMode.Loop : WrapMode.ClampForever;
            var started = animationComponent.Play(clipName);
            isContinuousPlayback = started;
            if (started && activeMaterialClip != null)
                activeMaterialClip.Apply(NormalizePlaybackTime(state.time, clip.length, state.wrapMode));
            return started;
        }

        public void Stop()
        {
            ResetPose();
        }

        public void Sample(string clipName, float time)
        {
            if (float.IsNaN(time) || float.IsInfinity(time))
                throw new ArgumentOutOfRangeException(nameof(time), time, "Sample time must be finite.");
            var clip = FindClip(clipName);
            EnsureInitialized();
            if (time < 0f || time > clip.length)
                throw new ArgumentOutOfRangeException(nameof(time), time, $"Sample time must be between 0 and clip duration {clip.length}.");
            animationComponent.Stop();
            isContinuousPlayback = false;
            RestoreInitialPose();
            SelectMaterialClip(clipName);
            clip.SampleAnimation(gameObject, time);
            activeMaterialClip?.Apply(time);
        }

        public void ResetPose()
        {
            EnsureInitialized();
            animationComponent.Stop();
            isContinuousPlayback = false;
            RestoreInitialPose();
            RestoreActiveMaterialClip();
        }

        private void LateUpdate()
        {
            if (!isContinuousPlayback || activeMaterialClip == null) return;
            var state = animationComponent[activeMaterialClip.ClipName];
            var clip = FindClip(activeMaterialClip.ClipName);
            activeMaterialClip.Apply(NormalizePlaybackTime(state.time, clip.length, state.wrapMode));
        }

        private void OnDisable()
        {
            isContinuousPlayback = false;
            RestoreActiveMaterialClip();
        }

        private void EnsureInitialized()
        {
            if (initialized) return;
            animationComponent = GetComponent<Animation>();
            animationComponent.playAutomatically = false;
            foreach (var clip in clips)
            {
                if (animationComponent.GetClip(clip.name) == null) animationComponent.AddClip(clip, clip.name);
            }
            if (!string.IsNullOrEmpty(defaultClipName)) animationComponent.clip = FindClip(defaultClipName);
            CaptureInitialPose();
            BuildMaterialClipRuntimes();
            initialized = true;
        }

        private AnimationClip FindClip(string clipName)
        {
            foreach (var clip in clips)
            {
                if (clip.name == clipName) return clip;
            }
            throw new ArgumentException($"Animation clip '{clipName}' is not registered on '{name}'.", nameof(clipName));
        }

        private void SelectMaterialClip(string clipName)
        {
            MaterialClipRuntime next = null;
            foreach (var runtime in materialClipRuntimes)
            {
                if (runtime.ClipName == clipName)
                {
                    next = runtime;
                    break;
                }
            }
            if (activeMaterialClip == next) return;
            RestoreActiveMaterialClip();
            activeMaterialClip = next;
        }

        private void RestoreActiveMaterialClip()
        {
            activeMaterialClip?.RestoreInitialValues();
            activeMaterialClip = null;
        }

        private void CaptureInitialPose()
        {
            initialTransforms = GetComponentsInChildren<Transform>(true);
            initialPositions = new Vector3[initialTransforms.Length];
            initialRotations = new Quaternion[initialTransforms.Length];
            initialScales = new Vector3[initialTransforms.Length];
            for (var index = 0; index < initialTransforms.Length; index++)
            {
                initialPositions[index] = initialTransforms[index].localPosition;
                initialRotations[index] = initialTransforms[index].localRotation;
                initialScales[index] = initialTransforms[index].localScale;
            }

            initialSkinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            initialBlendShapeWeights = new float[initialSkinnedRenderers.Length][];
            for (var rendererIndex = 0; rendererIndex < initialSkinnedRenderers.Length; rendererIndex++)
            {
                var renderer = initialSkinnedRenderers[rendererIndex];
                var count = renderer.sharedMesh == null ? 0 : renderer.sharedMesh.blendShapeCount;
                initialBlendShapeWeights[rendererIndex] = new float[count];
                for (var blendShapeIndex = 0; blendShapeIndex < count; blendShapeIndex++)
                    initialBlendShapeWeights[rendererIndex][blendShapeIndex] = renderer.GetBlendShapeWeight(blendShapeIndex);
            }
        }

        private void RestoreInitialPose()
        {
            for (var index = 0; index < initialTransforms.Length; index++)
            {
                initialTransforms[index].localPosition = initialPositions[index];
                initialTransforms[index].localRotation = initialRotations[index];
                initialTransforms[index].localScale = initialScales[index];
            }
            for (var rendererIndex = 0; rendererIndex < initialSkinnedRenderers.Length; rendererIndex++)
            for (var blendShapeIndex = 0; blendShapeIndex < initialBlendShapeWeights[rendererIndex].Length; blendShapeIndex++)
                initialSkinnedRenderers[rendererIndex].SetBlendShapeWeight(blendShapeIndex, initialBlendShapeWeights[rendererIndex][blendShapeIndex]);
        }

        private void BuildMaterialClipRuntimes()
        {
            materialClipRuntimes = new MaterialClipRuntime[materialClips.Length];
            for (var index = 0; index < materialClips.Length; index++)
                materialClipRuntimes[index] = new MaterialClipRuntime(materialClips[index]);
        }

        private static float NormalizePlaybackTime(float time, float duration, WrapMode wrapMode)
        {
            if (duration <= 0f) return 0f;
            return wrapMode == WrapMode.Loop ? Mathf.Repeat(time, duration) : Mathf.Clamp(time, 0f, duration);
        }

        private sealed class MaterialClipRuntime
        {
            private readonly MaterialSlotRuntime[] slots;

            public string ClipName { get; }

            public MaterialClipRuntime(ThreeUnityMaterialAnimationClip clip)
            {
                ClipName = clip.clipName;
                var groupedSlots = new List<MaterialSlotRuntime>();
                foreach (var binding in clip.bindings)
                {
                    MaterialSlotRuntime slot = null;
                    foreach (var candidate in groupedSlots)
                    {
                        if (candidate.Matches(binding.renderer, binding.materialSlot))
                        {
                            slot = candidate;
                            break;
                        }
                    }
                    if (slot == null)
                    {
                        slot = new MaterialSlotRuntime(binding.renderer, binding.materialSlot);
                        groupedSlots.Add(slot);
                    }
                    slot.Add(binding);
                }
                slots = groupedSlots.ToArray();
                foreach (var slot in slots) slot.Complete();
            }

            public void Apply(float time)
            {
                foreach (var slot in slots) slot.Apply(time);
            }

            public void RestoreInitialValues()
            {
                foreach (var slot in slots) slot.RestoreInitialValues();
            }
        }

        private sealed class MaterialSlotRuntime
        {
            private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
            private static readonly int ColorId = Shader.PropertyToID("_Color");
            private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
            private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
            private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
            private static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");
            private static readonly int BaseMapSTId = Shader.PropertyToID("_BaseMap_ST");
            private static readonly int MainTexSTId = Shader.PropertyToID("_MainTex_ST");

            private readonly Renderer renderer;
            private readonly int materialSlot;
            private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
            private readonly List<ThreeUnityMaterialAnimationBinding> pendingBindings = new List<ThreeUnityMaterialAnimationBinding>();
            private ThreeUnityMaterialAnimationBinding[] bindings = Array.Empty<ThreeUnityMaterialAnimationBinding>();

            public MaterialSlotRuntime(Renderer renderer, int materialSlot)
            {
                this.renderer = renderer;
                this.materialSlot = materialSlot;
            }

            public bool Matches(Renderer candidateRenderer, int candidateSlot) => renderer == candidateRenderer && materialSlot == candidateSlot;

            public void Add(ThreeUnityMaterialAnimationBinding binding) => pendingBindings.Add(binding);

            public void Complete()
            {
                bindings = pendingBindings.ToArray();
                pendingBindings.Clear();
            }

            public void Apply(float time)
            {
                if (renderer == null) return;
                renderer.GetPropertyBlock(block, materialSlot);
                foreach (var binding in bindings) SetValue(block, binding.property, Evaluate(binding, time));
                renderer.SetPropertyBlock(block, materialSlot);
            }

            public void RestoreInitialValues()
            {
                if (renderer == null) return;
                renderer.GetPropertyBlock(block, materialSlot);
                foreach (var binding in bindings) SetValue(block, binding.property, binding.initialValue);
                renderer.SetPropertyBlock(block, materialSlot);
            }

            private static Vector4 Evaluate(ThreeUnityMaterialAnimationBinding binding, float time)
            {
                var componentCount = GetComponentCount(binding.property);
                var times = binding.times;
                if (time <= times[0]) return ReadValue(binding.values, 0, componentCount);
                var lastIndex = times.Length - 1;
                if (time >= times[lastIndex]) return ReadValue(binding.values, lastIndex, componentCount);

                var low = 0;
                var high = lastIndex;
                while (high - low > 1)
                {
                    var middle = (low + high) / 2;
                    if (times[middle] <= time) low = middle;
                    else high = middle;
                }
                var range = times[high] - times[low];
                var t = range <= 0f ? 0f : (time - times[low]) / range;
                return Vector4.LerpUnclamped(ReadValue(binding.values, low, componentCount), ReadValue(binding.values, high, componentCount), t);
            }

            private static Vector4 ReadValue(float[] values, int keyIndex, int componentCount)
            {
                var offset = keyIndex * componentCount;
                return new Vector4(
                    values[offset],
                    componentCount > 1 ? values[offset + 1] : 0f,
                    componentCount > 2 ? values[offset + 2] : 0f,
                    componentCount > 3 ? values[offset + 3] : 0f);
            }

            private static int GetComponentCount(ThreeUnityMaterialAnimationProperty property)
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

            private static void SetValue(MaterialPropertyBlock target, ThreeUnityMaterialAnimationProperty property, Vector4 value)
            {
                switch (property)
                {
                    case ThreeUnityMaterialAnimationProperty.BaseColor:
                        var color = new Color(value.x, value.y, value.z, value.w);
                        target.SetColor(BaseColorId, color);
                        target.SetColor(ColorId, color);
                        break;
                    case ThreeUnityMaterialAnimationProperty.Emissive:
                        target.SetColor(EmissionColorId, new Color(value.x, value.y, value.z, 1f));
                        break;
                    case ThreeUnityMaterialAnimationProperty.Metallic:
                        target.SetFloat(MetallicId, value.x);
                        break;
                    case ThreeUnityMaterialAnimationProperty.Roughness:
                        var smoothness = 1f - Mathf.Clamp01(value.x);
                        target.SetFloat(SmoothnessId, smoothness);
                        target.SetFloat(GlossinessId, smoothness);
                        break;
                    case ThreeUnityMaterialAnimationProperty.BaseMapST:
                        var st = ThreeUnityMaterialAnimationUtility.ConvertBaseMapST(value);
                        target.SetVector(BaseMapSTId, st);
                        target.SetVector(MainTexSTId, st);
                        break;
                }
            }
        }
    }
}

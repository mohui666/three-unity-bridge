using System;
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

        private Animation animationComponent;

        public AnimationClip[] Clips => clips;
        public string DefaultClipName => defaultClipName;
        public bool PlayOnAwake => playOnAwake;
        public bool Loop => loop;

        public void Initialize(AnimationClip[] importedClips, string importedDefaultClipName, bool shouldPlayOnAwake, bool shouldLoop)
        {
            clips = importedClips;
            defaultClipName = importedDefaultClipName;
            playOnAwake = shouldPlayOnAwake;
            loop = shouldLoop;
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
            FindClip(clipName);
            EnsureInitialized();
            var state = animationComponent[clipName];
            state.wrapMode = shouldLoop ? WrapMode.Loop : WrapMode.Once;
            return animationComponent.Play(clipName);
        }

        public void Stop()
        {
            EnsureInitialized();
            animationComponent.Stop();
        }

        private void EnsureInitialized()
        {
            if (animationComponent == null)
            {
                animationComponent = GetComponent<Animation>();
                animationComponent.playAutomatically = false;
            }
            foreach (var clip in clips)
            {
                if (animationComponent.GetClip(clip.name) == null) animationComponent.AddClip(clip, clip.name);
            }
            if (!string.IsNullOrEmpty(defaultClipName)) animationComponent.clip = FindClip(defaultClipName);
        }

        private AnimationClip FindClip(string clipName)
        {
            foreach (var clip in clips)
            {
                if (clip.name == clipName) return clip;
            }
            throw new ArgumentException($"Animation clip '{clipName}' is not registered on '{name}'.", nameof(clipName));
        }
    }
}

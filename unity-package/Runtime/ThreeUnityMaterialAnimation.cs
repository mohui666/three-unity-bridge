using System;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    internal enum ThreeUnityMaterialAnimationProperty
    {
        BaseColor,
        Emissive,
        Metallic,
        Roughness,
        BaseMapST,
    }

    [Serializable]
    internal sealed class ThreeUnityMaterialAnimationBinding
    {
        public Renderer renderer;
        public int materialSlot;
        public ThreeUnityMaterialAnimationProperty property;
        public float[] times = Array.Empty<float>();
        public float[] values = Array.Empty<float>();
        public Vector4 initialValue;
    }

    [Serializable]
    internal sealed class ThreeUnityMaterialAnimationClip
    {
        public string clipName = string.Empty;
        public ThreeUnityMaterialAnimationBinding[] bindings = Array.Empty<ThreeUnityMaterialAnimationBinding>();
    }

    internal static class ThreeUnityMaterialAnimationUtility
    {
        public static Vector4 ConvertBaseMapST(Vector4 sourceValue) => sourceValue;
    }
}

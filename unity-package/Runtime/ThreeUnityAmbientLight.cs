using UnityEngine;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    public sealed class ThreeUnityAmbientLight : MonoBehaviour
    {
        public Color color = Color.white;
        public float intensity = 1f;
    }
}

using System;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    public sealed class ThreeUnityRuntimeProfile : MonoBehaviour
    {
        public string controller = "none";
        public string colliderMode = "none";
        public bool enableBlockEditing;
        public bool allowFly;
        public string hudStyle = "diagnostic";
        public float moveSpeed = 5.5f;
        public float sprintSpeed = 9f;
        public float flySpeed = 8f;
        public ThreeUnityHotbarItem[] hotbar = Array.Empty<ThreeUnityHotbarItem>();

        public void Initialize(
            string controllerValue,
            string colliderModeValue,
            bool blockEditing,
            bool fly,
            string hud,
            float move,
            float sprint,
            float flyMove,
            ThreeUnityHotbarItem[] items)
        {
            controller = string.IsNullOrEmpty(controllerValue) ? "none" : controllerValue;
            colliderMode = string.IsNullOrEmpty(colliderModeValue) ? "none" : colliderModeValue;
            enableBlockEditing = blockEditing;
            allowFly = fly;
            hudStyle = string.IsNullOrEmpty(hud) ? "diagnostic" : hud;
            moveSpeed = move > 0f ? move : 5.5f;
            sprintSpeed = sprint > 0f ? sprint : 9f;
            flySpeed = flyMove > 0f ? flyMove : 8f;
            hotbar = items ?? Array.Empty<ThreeUnityHotbarItem>();
        }
    }

    [Serializable]
    public sealed class ThreeUnityHotbarItem
    {
        public string name;
        public Color color = Color.white;
    }
}

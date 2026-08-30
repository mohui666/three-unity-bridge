using System;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    [Serializable]
    public sealed class ThreeUnityComponentDescriptor
    {
        public string type = string.Empty;
        [TextArea] public string dataJson = "{}";
    }

    [DisallowMultipleComponent]
    public sealed class ThreeUnityMetadata : MonoBehaviour
    {
        [SerializeField] private string sourceNodeId = string.Empty;
        [SerializeField] private int sourceLayersMask = 1;
        [SerializeField, TextArea] private string metadataJson = "{}";
        [SerializeField] private ThreeUnityComponentDescriptor[] components = Array.Empty<ThreeUnityComponentDescriptor>();

        public string SourceNodeId => sourceNodeId;
        public int SourceLayersMask => sourceLayersMask;
        public string MetadataJson => metadataJson;
        public ThreeUnityComponentDescriptor[] Components => components;

        public void Initialize(string nodeId, int layersMask, string json, ThreeUnityComponentDescriptor[] descriptors)
        {
            sourceNodeId = nodeId;
            sourceLayersMask = layersMask;
            metadataJson = string.IsNullOrEmpty(json) ? "{}" : json;
            components = descriptors ?? Array.Empty<ThreeUnityComponentDescriptor>();
        }
    }
}

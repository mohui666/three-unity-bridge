using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    [Serializable]
    public sealed class ThreeUnityComponentApplicationResult
    {
        public int Applied { get; internal set; }
        public int Unmapped { get; internal set; }
        public int Failed { get; internal set; }
    }

    [DisallowMultipleComponent]
    public sealed class ThreeUnityComponentApplicator : MonoBehaviour
    {
        [SerializeField] private bool applyOnAwake = true;

        private readonly HashSet<string> reportedUnmapped = new HashSet<string>(StringComparer.Ordinal);

        public bool ApplyOnAwake => applyOnAwake;
        public ThreeUnityComponentApplicationResult LastResult { get; private set; }

        private void Awake()
        {
            if (applyOnAwake) Apply();
        }

        public ThreeUnityComponentApplicationResult Apply()
        {
            var result = new ThreeUnityComponentApplicationResult();
            foreach (var metadata in GetComponentsInChildren<ThreeUnityMetadata>(true))
            {
                foreach (var descriptor in metadata.Components)
                {
                    var status = ThreeUnityComponentBindings.Apply(metadata.gameObject, descriptor, out var error);
                    var type = descriptor == null ? "<null>" : descriptor.type;
                    if (status == ThreeUnityComponentBindingStatus.Applied)
                    {
                        result.Applied++;
                        Debug.Log($"THREE_UNITY_COMPONENT_APPLIED type={type} sourceNodeId={metadata.SourceNodeId} target={metadata.gameObject.name}", metadata.gameObject);
                    }
                    else if (status == ThreeUnityComponentBindingStatus.Unmapped)
                    {
                        result.Unmapped++;
                        var diagnosticKey = $"{metadata.SourceNodeId}\n{type}\n{metadata.gameObject.GetInstanceID()}";
                        if (reportedUnmapped.Add(diagnosticKey))
                            Debug.LogWarning($"THREE_UNITY_COMPONENT_UNMAPPED type={type} sourceNodeId={metadata.SourceNodeId} target={metadata.gameObject.name}", metadata.gameObject);
                    }
                    else
                    {
                        result.Failed++;
                        Debug.LogError($"THREE_UNITY_COMPONENT_FAILED type={type} sourceNodeId={metadata.SourceNodeId} target={metadata.gameObject.name} error={error}", metadata.gameObject);
                    }
                }
            }

            LastResult = result;
            Debug.Log($"THREE_UNITY_COMPONENTS root={name} applied={result.Applied} unmapped={result.Unmapped} failed={result.Failed}", this);
            return result;
        }
    }
}

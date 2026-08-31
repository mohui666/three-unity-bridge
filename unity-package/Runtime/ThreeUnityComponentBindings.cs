using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    internal enum ThreeUnityComponentBindingStatus
    {
        Applied,
        Unmapped,
        Failed,
    }

    public static class ThreeUnityComponentBindings
    {
        private static readonly Dictionary<string, Action<GameObject, ThreeUnityComponentDescriptor>> Binders =
            new Dictionary<string, Action<GameObject, ThreeUnityComponentDescriptor>>(StringComparer.Ordinal);

        public static void Register<TData, TComponent>(string type, Action<TComponent, TData> configure)
            where TComponent : Component
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("Component binding type cannot be empty.", nameof(type));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            if (Binders.ContainsKey(type)) throw new InvalidOperationException($"Component binding '{type}' is already registered.");

            Binders.Add(type, (target, descriptor) =>
            {
                TData data;
                try
                {
                    data = JsonUtility.FromJson<TData>(descriptor.dataJson);
                    if (ReferenceEquals(data, null)) throw new ArgumentException("JSON produced no data object.");
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException($"Component binding '{type}' could not parse dataJson: {exception.Message}", exception);
                }

                var component = target.GetComponent<TComponent>();
                if (component == null) component = target.AddComponent<TComponent>();
                configure(component, data);
            });
        }

        internal static ThreeUnityComponentBindingStatus Apply(
            GameObject target,
            ThreeUnityComponentDescriptor descriptor,
            out string error)
        {
            error = string.Empty;
            if (descriptor == null)
            {
                error = "Component descriptor is null.";
                return ThreeUnityComponentBindingStatus.Failed;
            }
            if (string.IsNullOrEmpty(descriptor.type) || !Binders.TryGetValue(descriptor.type, out var binder))
                return ThreeUnityComponentBindingStatus.Unmapped;

            try
            {
                binder(target, descriptor);
                return ThreeUnityComponentBindingStatus.Applied;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return ThreeUnityComponentBindingStatus.Failed;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetBindings()
        {
            Binders.Clear();
        }
    }
}

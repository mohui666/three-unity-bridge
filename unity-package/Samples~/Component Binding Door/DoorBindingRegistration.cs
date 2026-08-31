using ThreeUnity.Bridge;
using UnityEngine;

namespace ThreeUnity.Bridge.Samples.ComponentBindingDoor
{
    internal static class DoorBindingRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterBindings()
        {
            ThreeUnityComponentBindings.Register<DoorData, Door>("Door", ConfigureDoor);
        }

        private static void ConfigureDoor(Door door, DoorData data)
        {
            door.Configure(data.openAngle, data.duration, data.startsOpen);
        }
    }
}

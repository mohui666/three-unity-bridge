# Component Binding Door

Repository checkouts do not track generated `.threeunity` payloads. Run `npm run samples:generate` before adding this package from disk; packed releases already include the asset.

This sample demonstrates an explicit component binding from a `.threeunity` descriptor to a project-owned `Door` MonoBehaviour.

Import the sample from Unity Package Manager, drag `component-binding-door.threeunity` into a Scene, and enter Play Mode. The binding adds `Door` to the `Door Pivot` node and configures it from the descriptor with a 95-degree opening angle, a 0.45-second duration, and `startsOpen` disabled. The door opens automatically when Play Mode starts.

`DoorBindingRegistration.cs` shows the required `RuntimeInitializeOnLoadMethod` registration. The imported metadata remains unchanged, while only the exact registered `Door` key can create this sample component.

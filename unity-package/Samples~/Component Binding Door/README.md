# Component Binding Door

This sample demonstrates an explicit component binding from a `.threeunity` descriptor to a project-owned `Door` MonoBehaviour.

Import the sample from Unity Package Manager, drag `component-binding-door.threeunity` into a Scene, and enter Play Mode. The binding adds `Door` to the `Door Pivot` node and configures it from the descriptor with a 95-degree opening angle, a 0.45-second duration, and `startsOpen` disabled. The door opens automatically when Play Mode starts.

`DoorBindingRegistration.cs` shows the required `RuntimeInitializeOnLoadMethod` registration. The imported metadata remains unchanged, while only the exact registered `Door` key can create this sample component.

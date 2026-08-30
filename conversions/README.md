# Open-source game conversion trials

These outputs are generated from live browser scenes after installing the packed `three-unity-bridge-0.1.0.tgz` into each upstream game.

The upstream repositories themselves are not redistributed here. The generated `.threeunity` files contain procedural geometry/material descriptions captured at runtime; source attribution and license are recorded in `report.json`.

Run the trial again after cloning the named repositories into `conversion-work/`, applying the small export hooks, installing their dependencies, and running:

```powershell
node conversion-tools/capture-games.mjs
```

Each resulting asset is checked with `three-unity validate`, imported by the Unity smoke project, given a reusable Unity-side control adapter, and built as a Windows Player. The adapters make the scenes movable/explorable; they do not claim that arbitrary JavaScript gameplay, DOM UI, audio, AI, custom shaders, or save systems were translated to C#.

# Three.js open-source game conversion results

Test date: 2026-08-30  
Unity: 6000.3.22f1  
Bridge: 0.1.0 packed npm tarball, installed into each upstream game

| Game | Upstream commit | License declaration | Export | Unity import evidence |
|---|---|---|---:|---:|
| [Voxel Frontier](https://github.com/Sunwood-ai-labs/threejs-voxel-frontier) | `63e455d0280dd68b1c7e7fec8b2f4fba2012df7f` | ISC in `package.json` | 4,651 nodes, 6 meshes, 6 materials | first-person controller, 4,640 block colliders |
| [LittleCubes](https://github.com/paugm/LittleCubes) | `7d1ff0c24e476c11771953f9ac2ea9be1e8ca552` | MIT `LICENSE` | 401 nodes, 203 meshes, 203 materials | detached camera retained, first-person controller, 203 mesh colliders |
| [Warptracker](https://github.com/ilrein/warptracker) | `71bbfbdfacd118196994b26da68eec1876d55c6b` | MIT `LICENSE` | 2,333 nodes, 693 meshes, 233 materials, 14 textures | detached camera retained, orbit/pan/zoom controller |

All three `.threeunity` documents passed `three-unity validate`. Unity compiled the package, imported all three assets in one project, loaded their generated GameObject hierarchies, created a camera/control entry for every scene, and passed the minimum mesh-count assertions.

All three passed real `StandaloneWindows64` Player builds: Voxel Frontier 93,385,915 bytes, LittleCubes 102,953,623 bytes, and Warptracker 112,886,419 bytes. The players were launched and visually checked; WASD input changed the Voxel first-person view and the Warptracker showcase camera.

## What the trials exposed

- Voxel Frontier required `InstancedMesh` support. The bridge now expands each instance transform while reusing one mesh/material record. Unity v0.1 currently creates one GameObject per instance; this is correct but not the desired production optimization for large voxel worlds. A later importer should preserve GPU instancing or combine chunks.
- LittleCubes' runtime-generated chunk geometry crossed without special-case game code. Its large JSON output shows why a binary payload/compression option is a priority.
- Warptracker crossed as an explorable showcase, but reports 24 unique warnings covering skinned meshes and `Points`. Skinning/animation is not reconstructed, and point effects are retained only as transforms. The ARPG's JavaScript combat, UI, audio, persistence, physics, and AI are not translated.

The exact machine-readable capture, file sizes, warnings, and browser diagnostics are in [`report.json`](report.json).

# Animated Skinned Mesh

Repository checkouts do not track generated `.threeunity` payloads. Run `npm run samples:generate` before adding this package from disk; packed releases already include the asset.

This sample is the generated output of `npm run example:animated`. It contains one ribbon mesh, a three-bone hierarchy, and the one-second looping `Ribbon Bend` clip.

Import the sample from Unity Package Manager, drag `animated-skinned-mesh.threeunity` into a Scene, and enter Play Mode. The imported `ThreeUnityAnimationPlayer` starts the default clip automatically, and the ribbon mesh bends with the animated bones.

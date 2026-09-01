# Morph Target Animation

Repository checkouts do not track generated `.threeunity` payloads. Run `npm run samples:generate` before adding this package from disk; packed releases already include the asset.

This sample is the generated output of `npm run example:morph`. It contains one morph-only mesh with the `Bulge` and `Twist` targets, a 25% initial Bulge weight, and the looping `Morph Cycle` clip.

Import the sample from Unity Package Manager, drag `morph-target-animation.threeunity` into a Scene, and enter Play Mode. The imported `ThreeUnityAnimationPlayer` starts the default clip automatically, and the mesh continuously deforms through Unity BlendShapes without an added sample behavior script.

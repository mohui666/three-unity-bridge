import { copyFile, mkdir, writeFile } from "node:fs/promises";

const generators = [
  "./generate-scene.mjs",
  "./generate-skinned-animation.mjs",
  "./generate-morph-animation.mjs",
  "./generate-material-animation.mjs",
  "./generate-instanced-mesh.mjs",
  "./generate-primitives.mjs",
  "./generate-component-binding.mjs",
  "./generate-texture-pipeline.mjs",
  "./generate-pbr-material-maps.mjs",
];

for (const generator of generators) await import(generator);

const triangleOutput = new URL("./output/imported-triangle-v1.threeunity", import.meta.url);
await mkdir(new URL("./output/", import.meta.url), { recursive: true });
await writeFile(triangleOutput, `${JSON.stringify(createVersionOneTriangle(), null, 2)}\n`, "utf8");

const sampleCopies = [
  ["./output/animated-skinned-mesh.threeunity", "../unity-package/Samples~/Animated Skinned Mesh/animated-skinned-mesh.threeunity"],
  ["./output/component-binding-door.threeunity", "../unity-package/Samples~/Component Binding Door/component-binding-door.threeunity"],
  ["./output/instanced-mesh-gpu.threeunity", "../unity-package/Samples~/GPU Instanced Mesh/instanced-mesh-gpu.threeunity"],
  ["./output/imported-triangle-v1.threeunity", "../unity-package/Samples~/Imported Triangle/triangle.threeunity"],
  ["./output/non-mesh-primitives.threeunity", "../unity-package/Samples~/Line Points Sprite/non-mesh-primitives.threeunity"],
  ["./output/material-uv-animation.threeunity", "../unity-package/Samples~/Material UV Animation/material-uv-animation.threeunity"],
  ["./output/morph-target-animation.threeunity", "../unity-package/Samples~/Morph Target Animation/morph-target-animation.threeunity"],
  ["./output/pbr-material-maps-v8.threeunity", "../unity-package/Samples~/PBR Material Maps/pbr-material-maps-v8.threeunity"],
  ["./output/texture-pipeline-v7.threeunity", "../unity-package/Samples~/Texture Sources and DataTexture/texture-pipeline-v7.threeunity"],
];

for (const [sourcePath, destinationPath] of sampleCopies) {
  const source = new URL(sourcePath, import.meta.url);
  const destination = new URL(destinationPath, import.meta.url);
  await mkdir(new URL("./", destination), { recursive: true });
  await copyFile(source, destination);
}

console.log(`Generated ${generators.length + 1} example assets and synchronized ${sampleCopies.length} Unity package samples.`);

function createVersionOneTriangle() {
  return {
    format: "three-unity-scene",
    version: 1,
    generator: "three-unity-bridge/0.1.0",
    name: "Imported Triangle",
    coordinateSystem: "threejs-right-handed-y-up",
    unitScaleMeters: 1,
    nodes: [
      {
        id: "node_scene",
        name: "Three Scene",
        parentId: "",
        visible: true,
        position: [0, 0, 0],
        quaternion: [0, 0, 0, 1],
        scale: [1, 1, 1],
        layersMask: 1,
        meshId: "",
        metadataJson: "{}",
        components: [],
      },
      {
        id: "node_triangle",
        name: "AI Triangle",
        parentId: "node_scene",
        visible: true,
        position: [0, 0, 0],
        quaternion: [0, 0, 0, 1],
        scale: [1, 1, 1],
        layersMask: 1,
        meshId: "mesh_triangle",
        metadataJson: JSON.stringify({ gameplayTag: "sample" }),
        components: [],
      },
    ],
    meshes: [
      {
        id: "mesh_triangle",
        name: "Triangle",
        positions: [-1, 0, 0, 1, 0, 0, 0, 1.5, 0],
        normals: [0, 0, 1, 0, 0, 1, 0, 0, 1],
        uv0: [0, 0, 1, 0, 0.5, 1],
        colors: [],
        indices: [0, 1, 2],
        groups: [],
        materialIds: ["material_blue"],
      },
    ],
    materials: [
      {
        id: "material_blue",
        name: "Bridge Blue",
        sourceType: "MeshStandardMaterial",
        baseColor: [0.08, 0.45, 1, 1],
        emissive: [0, 0, 0],
        metallic: 0.1,
        roughness: 0.45,
        opacity: 1,
        transparent: false,
        doubleSided: true,
        alphaCutoff: 0,
        unlit: false,
        baseColorTextureId: "",
        emissiveTextureId: "",
        normalTextureId: "",
        metallicRoughnessTextureId: "",
      },
    ],
    textures: [],
    warnings: [],
  };
}

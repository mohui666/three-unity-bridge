import assert from "node:assert/strict";
import test from "node:test";
import {
  AnimationClip,
  Bone,
  BoxGeometry,
  BufferGeometry,
  DataTexture,
  DirectionalLight,
  Float32BufferAttribute,
  InstancedMesh,
  Matrix4,
  Mesh,
  MeshLambertMaterial,
  MeshStandardMaterial,
  PerspectiveCamera,
  Quaternion,
  QuaternionKeyframeTrack,
  RGBAFormat,
  Scene,
  Skeleton,
  SkinnedMesh,
  SRGBColorSpace,
  Uint16BufferAttribute,
  UnsignedByteType,
  Vector3,
} from "three";
import { exportThreeUnity, validateDocument } from "../src/index.js";

test("exports a scene hierarchy, geometry, material, camera and light", async () => {
  const scene = new Scene();
  scene.name = "Bridge Test";
  scene.position.set(1, 2, 3);

  const pixels = new Uint8Array([255, 0, 0, 255]);
  const texture = new DataTexture(pixels, 1, 1, RGBAFormat, UnsignedByteType);
  texture.name = "red pixel";
  texture.colorSpace = SRGBColorSpace;
  const material = new MeshStandardMaterial({ color: 0x44aaff, metalness: 0.25, roughness: 0.75, map: texture });
  material.name = "blue metal";
  const cube = new Mesh(new BoxGeometry(2, 3, 4), material);
  cube.name = "Exported Cube";
  cube.position.set(4, 5, 6);
  cube.userData = {
    gameplayTag: "pickup",
    unity: { components: [{ type: "Spin", data: { degreesPerSecond: 30 } }] },
  };
  scene.add(cube);

  const camera = new PerspectiveCamera(60, 16 / 9, 0.2, 500);
  camera.name = "Main Camera";
  scene.add(camera);
  const light = new DirectionalLight(0xffffff, 2);
  light.name = "Sun";
  light.castShadow = true;
  scene.add(light);

  const document = await exportThreeUnity(scene, { unitScaleMeters: 0.5 });
  assert.deepEqual(validateDocument(document), { valid: true, errors: [] });
  assert.equal(document.name, "Bridge Test");
  assert.equal(document.nodes.length, 4);
  assert.equal(document.meshes.length, 1);
  assert.equal(document.materials.length, 1);
  assert.equal(document.textures.length, 1);
  assert.equal(document.textures[0].encoding, "rgba8");
  assert.equal(document.textures[0].data, "/wAA/w==");

  const cubeNode = document.nodes.find((node) => node.name === "Exported Cube");
  assert.ok(cubeNode);
  assert.equal(cubeNode.parentId, document.nodes[0].id);
  assert.deepEqual(cubeNode.position, [4, 5, 6]);
  assert.equal(cubeNode.components[0].type, "Spin");
  assert.equal(cubeNode.components[0].dataJson, "{\"degreesPerSecond\":30}");
  assert.equal(cubeNode.metadataJson, "{\"gameplayTag\":\"pickup\"}");
});

test("deduplicates shared geometry and material", async () => {
  const scene = new Scene();
  const geometry = new BoxGeometry();
  const material = new MeshStandardMaterial();
  scene.add(new Mesh(geometry, material), new Mesh(geometry, material));
  const document = await exportThreeUnity(scene);
  assert.equal(document.meshes.length, 1);
  assert.equal(document.materials.length, 1);
  assert.equal(document.nodes.filter((node) => node.meshId).length, 2);
});

test("keeps separate material bindings for shared geometry", async () => {
  const scene = new Scene();
  const geometry = new BoxGeometry();
  scene.add(
    new Mesh(geometry, new MeshStandardMaterial({ color: 0xff0000 })),
    new Mesh(geometry, new MeshStandardMaterial({ color: 0x00ff00 })),
  );
  const document = await exportThreeUnity(scene);
  assert.equal(document.meshes.length, 2);
  assert.equal(document.materials.length, 2);
  assert.notEqual(document.nodes[1].meshId, document.nodes[2].meshId);
});

test("preserves Three.js vertex-color material intent", async () => {
  const scene = new Scene();
  const material = new MeshLambertMaterial({ vertexColors: true });
  scene.add(new Mesh(new BoxGeometry(), material));

  const document = await exportThreeUnity(scene);

  assert.equal(document.materials[0].vertexColors, true);
});

test("exports reusable playable runtime configuration", async () => {
  const scene = new Scene();
  const document = await exportThreeUnity(scene, {
    runtime: {
      controller: "first-person",
      colliderMode: "mesh",
      enableBlockEditing: true,
      allowFly: true,
      hudStyle: "voxel-hotbar",
      hotbar: [{ name: "Grass", color: [0.4, 0.8, 0.2, 1] }],
    },
  });

  assert.equal(document.runtime.controller, "first-person");
  assert.equal(document.runtime.colliderMode, "mesh");
  assert.equal(document.runtime.hotbar[0].name, "Grass");
});

test("accepts pre-runtime format-v1 documents with Unity's safe defaults", async () => {
  const scene = new Scene();
  const legacyDocument = await exportThreeUnity(scene) as unknown as Record<string, unknown>;
  legacyDocument.version = 1;
  delete legacyDocument.runtime;
  delete legacyDocument.skins;
  delete legacyDocument.animations;
  delete legacyDocument.defaultAnimationId;
  delete legacyDocument.autoplayAnimation;

  assert.deepEqual(validateDocument(legacyDocument), { valid: true, errors: [] });
  assert.deepEqual(validateDocument({ ...legacyDocument, runtime: null }), {
    valid: false,
    errors: ["runtime must be an object when present."],
  });
});

test("exports a two-bone SkinnedMesh and baked AnimationClip with stable node references", async () => {
  const scene = new Scene();
  const geometry = new BufferGeometry();
  geometry.setAttribute("position", new Float32BufferAttribute([
    -0.4, 0, 0, 0.4, 0, 0,
    -0.4, 1, 0, 0.4, 1, 0,
    -0.4, 2, 0, 0.4, 2, 0,
  ], 3));
  geometry.setIndex([0, 1, 2, 1, 3, 2, 2, 3, 4, 3, 5, 4]);
  geometry.setAttribute("skinIndex", new Uint16BufferAttribute([
    0, 0, 0, 0, 0, 0, 0, 0,
    0, 1, 0, 0, 0, 1, 0, 0,
    1, 0, 0, 0, 1, 0, 0, 0,
  ], 4));
  geometry.setAttribute("skinWeight", new Float32BufferAttribute([
    2, 0, 0, 0, 2, 0, 0, 0,
    1, 1, 0, 0, 1, 1, 0, 0,
    3, 0, 0, 0, 3, 0, 0, 0,
  ], 4));
  geometry.computeVertexNormals();

  const rootBone = new Bone();
  rootBone.name = "Joint";
  const tipBone = new Bone();
  tipBone.name = "Joint";
  tipBone.position.y = 1;
  rootBone.add(tipBone);
  const mesh = new SkinnedMesh(geometry, new MeshStandardMaterial());
  mesh.name = "Animated Strip";
  mesh.add(rootBone);
  scene.add(mesh);
  mesh.bind(new Skeleton([rootBone, tipBone]));

  const bend = new Quaternion().setFromAxisAngle(new Vector3(0, 0, 1), Math.PI / 4);
  const clip = new AnimationClip("Bend", 1, [
    new QuaternionKeyframeTrack(`${tipBone.uuid}.quaternion`, [0, 0.5, 1], [
      0, 0, 0, 1,
      bend.x, bend.y, bend.z, bend.w,
      0, 0, 0, 1,
    ]),
  ]);
  scene.animations.push(clip);
  const originalQuaternion = tipBone.quaternion.toArray();

  const document = await exportThreeUnity(scene, {
    defaultAnimation: "Bend",
    autoplayAnimation: true,
    animationLoop: true,
    animationSampleRate: 10,
  });

  assert.deepEqual(validateDocument(document), { valid: true, errors: [] });
  assert.equal(document.version, 2);
  assert.equal(document.skins.length, 1);
  assert.equal(document.animations.length, 1);
  assert.equal(document.autoplayAnimation, true);
  assert.equal(document.defaultAnimationId, document.animations[0].id);
  assert.deepEqual(tipBone.quaternion.toArray(), originalQuaternion);

  const exportedMesh = document.meshes[0];
  assert.equal(exportedMesh.skinIndices.length, 6 * 4);
  assert.equal(exportedMesh.skinWeights.length, 6 * 4);
  assert.deepEqual(exportedMesh.skinWeights.slice(8, 12), [0.5, 0.5, 0, 0]);
  const skin = document.skins[0];
  const jointNodes = document.nodes.filter((node) => node.name === "Joint");
  assert.equal(jointNodes.length, 2);
  assert.deepEqual(new Set(skin.boneNodeIds), new Set(jointNodes.map((node) => node.id)));
  assert.equal(skin.inverseBindMatrices.length, 2 * 16);
  assert.equal(skin.bindMatrix.length, 16);
  assert.equal(document.nodes.find((node) => node.name === "Animated Strip")?.skinId, skin.id);
  const tipNode = jointNodes.find((node) => node.id === skin.boneNodeIds[1]);
  assert.ok(tipNode);
  const quaternionTrack = document.animations[0].tracks.find((track) => track.targetNodeId === tipNode.id && track.property === "quaternion");
  assert.ok(quaternionTrack);
  assert.equal(quaternionTrack.times.at(-1), 1);
  assert.equal(quaternionTrack.values.length, quaternionTrack.times.length * 4);

  const skinnedNode = document.nodes.find((node) => node.skinId === skin.id);
  assert.ok(skinnedNode);
  skinnedNode.meshId = "";
  const missingMeshValidation = validateDocument(document);
  assert.equal(missingMeshValidation.valid, false);
  assert.match(missingMeshValidation.errors.join("\n"), /must reference a mesh/);
});

test("omits invisible subtrees by default", async () => {
  const scene = new Scene();
  const hidden = new Mesh(new BoxGeometry(), new MeshStandardMaterial());
  hidden.visible = false;
  hidden.add(new Mesh(new BoxGeometry(), new MeshStandardMaterial()));
  scene.add(hidden);
  const document = await exportThreeUnity(scene);
  assert.equal(document.nodes.length, 1);
  assert.equal(document.meshes.length, 0);
});

test("exports render cameras that live outside the scene tree", async () => {
  const scene = new Scene();
  const camera = new PerspectiveCamera(72, 16 / 9, 0.1, 250);
  camera.name = "Detached Gameplay Camera";
  camera.position.set(3, 8, 12);

  const document = await exportThreeUnity(scene, { extraObjects: [camera, camera] });

  assert.equal(document.nodes.length, 2);
  const cameraNode = document.nodes.find((node) => node.name === "Detached Gameplay Camera");
  assert.ok(cameraNode?.camera);
  assert.equal(cameraNode.parentId, "");
  assert.deepEqual(cameraNode.position, [3, 8, 12]);
  assert.equal(cameraNode.camera.fov, 72);
});

test("expands InstancedMesh transforms without duplicating geometry", async () => {
  const scene = new Scene();
  const instances = new InstancedMesh(new BoxGeometry(), new MeshStandardMaterial(), 3);
  instances.name = "Voxel";
  for (let index = 0; index < 3; index += 1) instances.setMatrixAt(index, new Matrix4().makeTranslation(index, index * 2, -index));
  scene.add(instances);
  const document = await exportThreeUnity(scene);
  assert.equal(document.meshes.length, 1);
  assert.equal(document.nodes.length, 5);
  assert.equal(document.nodes[1].meshId, "");
  assert.deepEqual(document.nodes.slice(2).map((node) => node.position.map((value) => value || 0)), [[0, 0, 0], [1, 2, -1], [2, 4, -2]]);
});

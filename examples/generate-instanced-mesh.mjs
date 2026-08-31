import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import {
  AnimationClip,
  BoxGeometry,
  Color,
  DirectionalLight,
  InstancedMesh,
  Matrix4,
  MeshStandardMaterial,
  PerspectiveCamera,
  Quaternion,
  QuaternionKeyframeTrack,
  Scene,
  Vector3,
} from "three";
import { exportThreeUnity, validateDocument } from "../dist/index.js";

const scene = new Scene();
scene.name = "GPU Instanced Mesh";
scene.uuid = "60000000-0000-4000-8000-000000000001";

const camera = new PerspectiveCamera(52, 16 / 9, 0.1, 120);
camera.name = "Instanced Field Camera";
camera.uuid = "60000000-0000-4000-8000-000000000002";
camera.position.set(24, 28, 32);
camera.lookAt(0, 0, 0);
scene.add(camera);

const sun = new DirectionalLight(0xffffff, 2.2);
sun.name = "Instanced Field Sun";
sun.uuid = "60000000-0000-4000-8000-000000000003";
sun.position.set(8, 16, 10);
sun.castShadow = true;
scene.add(sun);

const geometry = new BoxGeometry(1, 1, 1);
geometry.name = "Two Material Instance Cube";
geometry.uuid = "60000000-0000-4000-8000-000000000004";
geometry.clearGroups();
geometry.addGroup(0, 18, 0);
geometry.addGroup(18, 18, 1);

const upperMaterial = new MeshStandardMaterial({ color: 0xd6ecff, metalness: 0.08, roughness: 0.42 });
upperMaterial.name = "Cool Instance Surface";
upperMaterial.uuid = "60000000-0000-4000-8000-000000000005";
const lowerMaterial = new MeshStandardMaterial({ color: 0xffd4b8, metalness: 0.02, roughness: 0.68 });
lowerMaterial.name = "Warm Instance Surface";
lowerMaterial.uuid = "60000000-0000-4000-8000-000000000006";

const instanceCount = 2500;
const gridWidth = 50;
const instances = new InstancedMesh(geometry, [upperMaterial, lowerMaterial], instanceCount);
instances.name = "GPU Instanced Field";
instances.uuid = "60000000-0000-4000-8000-000000000007";

const matrix = new Matrix4();
const position = new Vector3();
const rotation = new Quaternion();
const scale = new Vector3();
const up = new Vector3(0, 1, 0);
const color = new Color();
for (let index = 0; index < instanceCount; index += 1) {
  const column = index % gridWidth;
  const row = Math.floor(index / gridWidth);
  if (index === instanceCount - 1) {
    matrix.set(
      1, 0.2, 0, 17.15,
      0, 1, 0.1, 0.35,
      0, 0, 1, 17.15,
      0, 0, 0, 1,
    );
  } else {
    position.set((column - 24.5) * 0.7, 0.35 + (index % 7) * 0.06, (row - 24.5) * 0.7);
    rotation.setFromAxisAngle(up, (index % 16) * Math.PI / 8);
    scale.set(0.28 + (index % 5) * 0.025, 0.34 + (index % 9) * 0.025, 0.28 + (index * 3 % 5) * 0.025);
    matrix.compose(position, rotation, scale);
  }
  instances.setMatrixAt(index, matrix);
  color.setRGB((column + 1) / gridWidth, (row + 1) / gridWidth, (index % 17 + 1) / 17);
  instances.setColorAt(index, color);
}
instances.instanceMatrix.needsUpdate = true;
instances.instanceColor.needsUpdate = true;
scene.add(instances);

const halfTurn = new Quaternion().setFromAxisAngle(up, Math.PI / 10);
const clip = new AnimationClip("Instanced Field Orbit", 4, [
  new QuaternionKeyframeTrack(`${instances.uuid}.quaternion`, [0, 2, 4], [
    0, 0, 0, 1,
    ...halfTurn.toArray(),
    0, 0, 0, 1,
  ]),
]);
clip.uuid = "60000000-0000-4000-8000-000000000008";
scene.animations.push(clip);

const sourceMatrices = Array.from(instances.instanceMatrix.array);
const sourceColors = Array.from(instances.instanceColor.array);
const document = await exportThreeUnity(scene, {
  defaultAnimation: clip,
  autoplayAnimation: true,
  animationLoop: true,
  animationSampleRate: 30,
});
const validation = validateDocument(document);
if (!validation.valid) throw new Error(`Generated GPU instancing example is invalid: ${validation.errors.join(" ")}`);

const node = document.nodes.find((candidate) => candidate.name === instances.name);
const record = document.instancedMeshes[0];
assert.equal(document.version, 7);
assert.ok(node);
assert.equal(node.instancedMeshId, record.id);
assert.equal(record.name, "GPU Instanced Field Instances");
assert.equal(record.count, instanceCount);
assert.equal(record.matrices.length, instanceCount * 16);
assert.equal(record.colors.length, instanceCount * 4);
assert.deepEqual(record.matrices.slice(-16), sourceMatrices.slice(-16));
assert.ok(Math.abs(record.matrices.at(-12) - 0.2) < 1e-6);
assert.ok(Math.abs(record.matrices.at(-7) - 0.1) < 1e-6);
assert.deepEqual(record.colors.slice(-4), [...sourceColors.slice(-3), 1]);
assert.deepEqual(document.meshes[0].groups, [
  { start: 0, count: 18, materialIndex: 0 },
  { start: 18, count: 18, materialIndex: 1 },
]);
assert.ok(document.animations[0].tracks.some((track) => track.targetNodeId === node.id && track.property === "quaternion"));
assert.deepEqual(Array.from(instances.instanceMatrix.array), sourceMatrices);
assert.deepEqual(Array.from(instances.instanceColor.array), sourceColors);

await mkdir(new URL("./output/", import.meta.url), { recursive: true });
const output = new URL("./output/instanced-mesh-gpu.threeunity", import.meta.url);
await writeFile(output, `${JSON.stringify(document, null, 2)}\n`, "utf8");

const totalInstances = document.instancedMeshes.reduce((sum, candidate) => sum + candidate.count, 0);
console.log(`Wrote ${output.pathname}`);
console.log(`format: ${document.version}`);
console.log(`instanced records: ${document.instancedMeshes.length}`);
console.log(`instances: ${totalInstances}`);
console.log(`node count: ${document.nodes.length}`);
console.log(`colors: ${record.colors.length > 0 ? "yes" : "no"}`);
console.log(`expected batches: ${Math.ceil(record.count / 1023)}`);

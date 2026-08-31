import { mkdir, writeFile } from "node:fs/promises";
import {
  AnimationClip,
  Bone,
  BufferGeometry,
  DoubleSide,
  Float32BufferAttribute,
  MeshStandardMaterial,
  Quaternion,
  QuaternionKeyframeTrack,
  Scene,
  Skeleton,
  SkinnedMesh,
  Uint16BufferAttribute,
  Vector3,
} from "three";
import { exportThreeUnityJson, validateDocument } from "../dist/index.js";

const scene = new Scene();
scene.name = "Animated Skinned Mesh";

const geometry = new BufferGeometry();
geometry.name = "Three Bone Ribbon";
geometry.setAttribute("position", new Float32BufferAttribute([
  -0.35, 0, 0, 0.35, 0, 0,
  -0.35, 0.75, 0, 0.35, 0.75, 0,
  -0.35, 1.5, 0, 0.35, 1.5, 0,
  -0.35, 2.25, 0, 0.35, 2.25, 0,
], 3));
geometry.setIndex([
  0, 1, 2, 1, 3, 2,
  2, 3, 4, 3, 5, 4,
  4, 5, 6, 5, 7, 6,
]);
geometry.setAttribute("skinIndex", new Uint16BufferAttribute([
  0, 0, 0, 0, 0, 0, 0, 0,
  0, 1, 0, 0, 0, 1, 0, 0,
  1, 2, 0, 0, 1, 2, 0, 0,
  2, 0, 0, 0, 2, 0, 0, 0,
], 4));
geometry.setAttribute("skinWeight", new Float32BufferAttribute([
  1, 0, 0, 0, 1, 0, 0, 0,
  0.25, 0.75, 0, 0, 0.25, 0.75, 0, 0,
  0.25, 0.75, 0, 0, 0.25, 0.75, 0, 0,
  1, 0, 0, 0, 1, 0, 0, 0,
], 4));
geometry.computeVertexNormals();

const rootBone = new Bone();
rootBone.name = "Ribbon Root";
const middleBone = new Bone();
middleBone.name = "Ribbon/Middle";
middleBone.position.y = 0.75;
const tipBone = new Bone();
tipBone.name = "Ribbon Tip";
tipBone.position.y = 0.75;
rootBone.add(middleBone);
middleBone.add(tipBone);

const ribbon = new SkinnedMesh(
  geometry,
  new MeshStandardMaterial({ color: 0x39aee8, roughness: 0.55, metalness: 0.05, side: DoubleSide }),
);
ribbon.name = "Bending Ribbon";
ribbon.add(rootBone);
scene.add(ribbon);
ribbon.bind(new Skeleton([rootBone, middleBone, tipBone]));

const axis = new Vector3(0, 0, 1);
const middleBend = new Quaternion().setFromAxisAngle(axis, Math.PI / 6);
const tipBend = new Quaternion().setFromAxisAngle(axis, -Math.PI * 0.3);
const identity = [0, 0, 0, 1];
const clip = new AnimationClip("Ribbon Bend", 1, [
  new QuaternionKeyframeTrack(`${middleBone.uuid}.quaternion`, [0, 0.5, 1], [
    ...identity, middleBend.x, middleBend.y, middleBend.z, middleBend.w, ...identity,
  ]),
  new QuaternionKeyframeTrack(`${tipBone.uuid}.quaternion`, [0, 0.5, 1], [
    ...identity, tipBend.x, tipBend.y, tipBend.z, tipBend.w, ...identity,
  ]),
]);
scene.animations.push(clip);

const json = await exportThreeUnityJson(scene, {
  defaultAnimation: clip,
  autoplayAnimation: true,
  animationLoop: true,
  animationSampleRate: 30,
});
const document = JSON.parse(json);
const validation = validateDocument(document);
if (!validation.valid) throw new Error(`Generated animated example is invalid: ${validation.errors.join(" ")}`);

await mkdir(new URL("./output/", import.meta.url), { recursive: true });
const output = new URL("./output/animated-skinned-mesh.threeunity", import.meta.url);
await writeFile(output, `${json}\n`, "utf8");
console.log(`Wrote ${output.pathname}`);

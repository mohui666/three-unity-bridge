import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import {
  AnimationClip,
  BoxGeometry,
  ColorKeyframeTrack,
  DataTexture,
  Mesh,
  MeshStandardMaterial,
  NumberKeyframeTrack,
  RepeatWrapping,
  RGBAFormat,
  Scene,
  SRGBColorSpace,
  UnsignedByteType,
  VectorKeyframeTrack,
} from "three";
import { exportThreeUnityJson, validateDocument } from "../dist/index.js";

const scene = new Scene();
scene.name = "Material UV Animation";
scene.uuid = "40000000-0000-4000-8000-000000000001";

const quadrantPixels = new Uint8Array([
  255, 32, 32, 255, 255, 32, 32, 255, 32, 255, 64, 255, 32, 255, 64, 255,
  255, 32, 32, 255, 255, 32, 32, 255, 32, 255, 64, 255, 32, 255, 64, 255,
  32, 64, 255, 255, 32, 64, 255, 255, 255, 224, 32, 255, 255, 224, 32, 255,
  32, 64, 255, 255, 32, 64, 255, 255, 255, 224, 32, 255, 255, 224, 32, 255,
]);
const texture = new DataTexture(quadrantPixels, 4, 4, RGBAFormat, UnsignedByteType);
texture.name = "Asymmetric Quadrant Texture";
texture.uuid = "40000000-0000-4000-8000-000000000002";
texture.colorSpace = SRGBColorSpace;
texture.wrapS = RepeatWrapping;
texture.wrapT = RepeatWrapping;
texture.repeat.set(2, 2);
texture.offset.set(0.125, 0.25);
texture.needsUpdate = true;

const animatedMaterial = new MeshStandardMaterial({ map: texture, transparent: true });
animatedMaterial.name = "Shared Animated Surface";
animatedMaterial.uuid = "40000000-0000-4000-8000-000000000003";
animatedMaterial.color.setRGB(0.25, 0.55, 1);
animatedMaterial.opacity = 0.9;
animatedMaterial.emissive.setRGB(0, 0, 0);
animatedMaterial.metalness = 0.12;
animatedMaterial.roughness = 0.75;

const accentMaterial = new MeshStandardMaterial({ color: 0x30343f, metalness: 0.25, roughness: 0.4 });
accentMaterial.name = "Static Accent Surface";
accentMaterial.uuid = "40000000-0000-4000-8000-000000000004";

const primaryGeometry = new BoxGeometry(1.5, 1.5, 1.5);
primaryGeometry.name = "Single Material Cube";
primaryGeometry.uuid = "40000000-0000-4000-8000-000000000005";
primaryGeometry.clearGroups();
const primary = new Mesh(primaryGeometry, animatedMaterial);
primary.name = "Shared Material Cube";
primary.uuid = "40000000-0000-4000-8000-000000000006";
primary.position.x = -1.25;

const groupedGeometry = new BoxGeometry(1.5, 1.5, 1.5);
groupedGeometry.name = "Two Slot Cube";
groupedGeometry.uuid = "40000000-0000-4000-8000-000000000007";
groupedGeometry.clearGroups();
groupedGeometry.addGroup(0, 18, 0);
groupedGeometry.addGroup(18, 18, 1);
const grouped = new Mesh(groupedGeometry, [accentMaterial, animatedMaterial]);
grouped.name = "Grouped Shared Material Cube";
grouped.uuid = "40000000-0000-4000-8000-000000000008";
grouped.position.x = 1.25;
scene.add(primary, grouped);

const duration = 3;
const times = [0, duration / 2, duration];
const clip = new AnimationClip("Material UV Cycle", duration, [
  new ColorKeyframeTrack(`${primary.uuid}.material.color`, times, [
    0.25, 0.55, 1,
    1, 0.12, 0.04,
    0.25, 0.55, 1,
  ]),
  new NumberKeyframeTrack(`${primary.uuid}.material.opacity`, times, [0.9, 0.35, 0.9]),
  new ColorKeyframeTrack(`${primary.uuid}.material.emissive`, times, [
    0, 0, 0,
    0.9, 0.24, 0.05,
    0, 0, 0,
  ]),
  new NumberKeyframeTrack(`${primary.uuid}.material.roughness`, times, [0.75, 0.15, 0.75]),
  new VectorKeyframeTrack(`${primary.uuid}.map.offset`, times, [
    0.125, 0.25,
    0.625, 0.75,
    1.125, 1.25,
  ]),
  new VectorKeyframeTrack(`${primary.uuid}.map.repeat`, times, [
    2, 2,
    3.25, 1.25,
    2, 2,
  ]),
]);
clip.uuid = "40000000-0000-4000-8000-000000000009";
scene.animations.push(clip);

const originalState = {
  color: animatedMaterial.color.toArray(),
  opacity: animatedMaterial.opacity,
  emissive: animatedMaterial.emissive.toArray(),
  metalness: animatedMaterial.metalness,
  roughness: animatedMaterial.roughness,
  offset: texture.offset.toArray(),
  repeat: texture.repeat.toArray(),
};
const json = await exportThreeUnityJson(scene, {
  defaultAnimation: clip,
  autoplayAnimation: true,
  animationLoop: true,
  animationSampleRate: 30,
});
const document = JSON.parse(json);
const validation = validateDocument(document);
if (!validation.valid) throw new Error(`Generated material animation example is invalid: ${validation.errors.join(" ")}`);

assert.deepEqual(animatedMaterial.color.toArray(), originalState.color);
assert.equal(animatedMaterial.opacity, originalState.opacity);
assert.deepEqual(animatedMaterial.emissive.toArray(), originalState.emissive);
assert.equal(animatedMaterial.metalness, originalState.metalness);
assert.equal(animatedMaterial.roughness, originalState.roughness);
assert.deepEqual(texture.offset.toArray(), originalState.offset);
assert.deepEqual(texture.repeat.toArray(), originalState.repeat);

const primaryNode = document.nodes.find((node) => node.name === primary.name);
const groupedNode = document.nodes.find((node) => node.name === grouped.name);
assert.ok(primaryNode && groupedNode);
assert.deepEqual(document.materials.find((material) => material.name === animatedMaterial.name)?.baseColorTextureST, [2, 2, 0.125, 0.25]);
assert.equal(document.textures[0].wrapS, "repeat");
assert.equal(document.textures[0].wrapT, "repeat");
for (const property of ["materialBaseColor", "materialEmissive", "materialRoughness", "materialBaseMapST"]) {
  const bindings = document.animations[0].tracks
    .filter((track) => track.property === property)
    .map((track) => `${track.targetNodeId}:${track.materialIndex}`);
  assert.ok(bindings.includes(`${primaryNode.id}:0`));
  assert.ok(bindings.includes(`${groupedNode.id}:1`));
}

await mkdir(new URL("./output/", import.meta.url), { recursive: true });
const output = new URL("./output/material-uv-animation.threeunity", import.meta.url);
await writeFile(output, `${json}\n`, "utf8");
console.log(`Wrote ${output.pathname}`);

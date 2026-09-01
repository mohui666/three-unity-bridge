import assert from "node:assert/strict";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import {
  AmbientLight,
  AnimationClip,
  DataTexture,
  DirectionalLight,
  LinearFilter,
  LinearMipmapLinearFilter,
  Mesh,
  MeshStandardMaterial,
  NoColorSpace,
  NumberKeyframeTrack,
  PerspectiveCamera,
  PointLight,
  Quaternion,
  QuaternionKeyframeTrack,
  RepeatWrapping,
  RGBAFormat,
  Scene,
  SphereGeometry,
  SRGBColorSpace,
  UnsignedByteType,
} from "three";
import { exportThreeUnity, validateDocument } from "../dist/index.js";

const outputDirectory = new URL("./output/", import.meta.url);
const output = new URL("./output/pbr-material-maps-v8.threeunity", import.meta.url);

const sharedOrm = createTexture("Shared ORM Source", 1, createSharedOrmPixels(), NoColorSpace, [1.5, 1], [0.125, 0.05]);
const metalness = createTexture("Separate Metalness Source", 2, createMetalnessPixels(), NoColorSpace, [1.25, 1.25], [0.1, 0.15]);
const roughness = createTexture("Separate Roughness Source", 3, createRoughnessPixels(), NoColorSpace, [1.25, 1.25], [0.1, 0.15]);
const normal = createTexture("Tangent Normal Source", 4, createNormalPixels(), NoColorSpace, [1.75, 1.25], [0.05, 0.1]);
const emissive = createTexture("Emissive Checker Source", 5, createEmissivePixels(), SRGBColorSpace, [2, 2], [0.125, 0.125]);
const textures = [sharedOrm, metalness, roughness, normal, emissive];

const scalarMaterial = new MeshStandardMaterial({
  color: 0xc89b68,
  metalness: 0.15,
  roughness: 0.78,
});
configureMaterial(scalarMaterial, "Scalar Only Baseline", 10);

const sharedMaterial = new MeshStandardMaterial({
  color: 0xcfd5dc,
  metalness: 0.78,
  roughness: 0.86,
  metalnessMap: sharedOrm,
  roughnessMap: sharedOrm,
  normalMap: normal,
});
sharedMaterial.normalScale.set(0.65, 0.65);
configureMaterial(sharedMaterial, "Shared ORM PBR", 11);

const separateMaterial = new MeshStandardMaterial({
  color: 0xb8c7d9,
  metalness: 0.72,
  roughness: 0.82,
  metalnessMap: metalness,
  roughnessMap: roughness,
  normalMap: normal,
});
separateMaterial.normalScale.set(0.35, 1);
configureMaterial(separateMaterial, "Separate PBR Maps", 12);

const emissiveMaterial = new MeshStandardMaterial({
  color: 0x596579,
  metalness: 0.22,
  roughness: 0.42,
  normalMap: normal,
  emissiveMap: emissive,
  emissive: 0xff4422,
  emissiveIntensity: 2.4,
});
emissiveMaterial.normalScale.set(1, -0.5);
configureMaterial(emissiveMaterial, "Normal Emissive Pulse", 13);
const materials = [scalarMaterial, sharedMaterial, separateMaterial, emissiveMaterial];

const scene = new Scene();
scene.name = "PBR Material Maps";
scene.uuid = stableUuid(20);

const camera = new PerspectiveCamera(42, 16 / 9, 0.1, 100);
camera.name = "PBR Camera";
camera.uuid = stableUuid(21);
camera.position.set(0, 1.4, 13);
camera.lookAt(0, 0, 0);
scene.add(camera);

const ambient = new AmbientLight(0xffffff, 0.5);
ambient.name = "PBR Ambient";
ambient.uuid = stableUuid(22);
scene.add(ambient);

const keyLight = new DirectionalLight(0xfff4df, 1.6);
keyLight.name = "PBR Key Light";
keyLight.uuid = stableUuid(23);
keyLight.position.set(4, 6, 5);
scene.add(keyLight);

const pointLight = new PointLight(0x77aaff, 4.5, 24, 2);
pointLight.name = "PBR Rim Light";
pointLight.uuid = stableUuid(24);
pointLight.position.set(-4, 2.5, 4);
scene.add(pointLight);

const tangentGeometry = new SphereGeometry(1.12, 24, 16);
tangentGeometry.name = "Sphere With Source Tangents";
tangentGeometry.uuid = stableUuid(30);
tangentGeometry.computeTangents();

const missingTangentGeometry = new SphereGeometry(1.12, 24, 16);
missingTangentGeometry.name = "Sphere Recalculate Tangents";
missingTangentGeometry.uuid = stableUuid(31);

const positions = [-4.5, -1.5, 1.5, 4.5];
const meshes = materials.map((material, index) => {
  const geometry = index < 2 ? tangentGeometry : missingTangentGeometry;
  const mesh = new Mesh(geometry, material);
  mesh.name = ["Scalar Sphere", "Shared ORM Sphere", "Separate Maps Sphere", "Normal Emissive Sphere"][index];
  mesh.uuid = stableUuid(40 + index);
  mesh.position.set(positions[index], 0, 0);
  scene.add(mesh);
  return mesh;
});

const duration = 4;
const times = [0, duration / 2, duration];
const halfTurn = new Quaternion().setFromAxisAngle({ x: 0, y: 1, z: 0 }, Math.PI);
const fullTurn = new Quaternion().setFromAxisAngle({ x: 0, y: 1, z: 0 }, Math.PI * 2);
const animatedMesh = meshes[3];
const clip = new AnimationClip("PBR Rotation and Emission", duration, [
  new QuaternionKeyframeTrack(`${animatedMesh.uuid}.quaternion`, times, [
    0, 0, 0, 1,
    ...halfTurn.toArray(),
    ...fullTurn.toArray(),
  ]),
  new NumberKeyframeTrack(`${animatedMesh.uuid}.material.emissiveIntensity`, times, [2.4, 5, 2.4]),
]);
clip.uuid = stableUuid(50);
scene.animations.push(clip);

const sourceState = {
  materials: materials.map((material) => ({
    emissive: material.emissive.toArray(),
    emissiveIntensity: material.emissiveIntensity,
    metalness: material.metalness,
    roughness: material.roughness,
    normalScale: material.normalScale.toArray(),
  })),
  textures: textures.map((texture) => ({
    bytes: Array.from(texture.image.data),
    colorSpace: texture.colorSpace,
    offset: texture.offset.toArray(),
    repeat: texture.repeat.toArray(),
  })),
};

const document = await exportThreeUnity(scene, {
  defaultAnimation: clip,
  autoplayAnimation: true,
  animationLoop: true,
  animationSampleRate: 30,
});
const validation = validateDocument(document);
if (!validation.valid) throw new Error(`Generated PBR example is invalid: ${validation.errors.join(" ")}`);

assert.equal(document.version, 8);
assert.ok(document.materials.filter((material) => material.metalnessTextureId).length >= 2);
assert.ok(document.materials.filter((material) => material.roughnessTextureId).length >= 2);
assert.ok(document.materials.some((material) => material.normalTextureId));
assert.ok(document.materials.some((material) => material.emissiveIntensity > 1));
assert.ok(document.meshes.length >= 4);

const exportedShared = findMaterial(document, sharedMaterial.name);
const exportedSeparate = findMaterial(document, separateMaterial.name);
assert.equal(exportedShared.metalnessTextureId, exportedShared.roughnessTextureId);
assert.equal(exportedShared.metallicRoughnessTextureId, exportedShared.metalnessTextureId);
assert.notEqual(exportedSeparate.metalnessTextureId, exportedSeparate.roughnessTextureId);
assert.equal(exportedSeparate.metallicRoughnessTextureId, "");
assert.ok(document.meshes.some((mesh) => mesh.tangents.length > 0));
assert.ok(document.meshes.some((mesh) => mesh.tangents.length === 0));

assert.deepEqual(materials.map((material) => ({
  emissive: material.emissive.toArray(),
  emissiveIntensity: material.emissiveIntensity,
  metalness: material.metalness,
  roughness: material.roughness,
  normalScale: material.normalScale.toArray(),
})), sourceState.materials);
assert.deepEqual(textures.map((texture) => ({
  bytes: Array.from(texture.image.data),
  colorSpace: texture.colorSpace,
  offset: texture.offset.toArray(),
  repeat: texture.repeat.toArray(),
})), sourceState.textures);

const json = `${JSON.stringify(document, null, 2)}\n`;
assert.equal(json.includes("http://"), false);
assert.equal(json.includes("https://"), false);
assert.equal(json.includes("file://"), false);
assert.equal(json.includes("threeUnitySource"), false);

await mkdir(outputDirectory, { recursive: true });
await writeFile(output, json, "utf8");
const persisted = JSON.parse(await readFile(output, "utf8"));
const persistedValidation = validateDocument(persisted);
if (!persistedValidation.valid) throw new Error(`Persisted PBR example is invalid: ${persistedValidation.errors.join(" ")}`);

console.log("format: 8");
console.log("PBR materials: scalar, shared ORM, separate masks, normal + emission");
console.log("metalness: Three.js B channel → Unity derived R");
console.log("roughness: Three.js G channel → Unity derived 1 - A");
console.log("normal scales: uniform, non-uniform, negative Y");
console.log("emission: intensity pulse baked into materialEmissive animation");
console.log("external sources: none");
console.log(`Wrote ${output.pathname}`);

function createTexture(name, uuidIndex, pixels, colorSpace, repeat, offset) {
  const texture = new DataTexture(pixels, 8, 8, RGBAFormat, UnsignedByteType);
  texture.name = name;
  texture.uuid = stableUuid(uuidIndex);
  texture.colorSpace = colorSpace;
  texture.flipY = false;
  texture.wrapS = RepeatWrapping;
  texture.wrapT = RepeatWrapping;
  texture.magFilter = LinearFilter;
  texture.minFilter = LinearMipmapLinearFilter;
  texture.generateMipmaps = true;
  texture.anisotropy = 4;
  texture.repeat.set(...repeat);
  texture.offset.set(...offset);
  texture.needsUpdate = true;
  return texture;
}

function configureMaterial(material, name, uuidIndex) {
  material.name = name;
  material.uuid = stableUuid(uuidIndex);
}

function createSharedOrmPixels() {
  return createPixels((x, y) => [48, Math.round(24 + x * 30), (x < 4) === (y < 4) ? 224 : 40, 255]);
}

function createMetalnessPixels() {
  return createPixels((x, y) => [0, 0, (x + y) % 2 === 0 ? 235 : 25, 255]);
}

function createRoughnessPixels() {
  return createPixels((x) => [0, Math.round(20 + x * 32), 0, 255]);
}

function createNormalPixels() {
  return createPixels((x, y) => {
    const nx = x < 4 ? 0.45 : -0.45;
    const ny = y < 4 ? 0.25 : -0.25;
    const nz = Math.sqrt(1 - nx * nx - ny * ny);
    return [encodeNormal(nx), encodeNormal(ny), encodeNormal(nz), 255];
  });
}

function createEmissivePixels() {
  const colors = [
    [0, 0, 0, 255],
    [255, 24, 12, 255],
    [18, 60, 255, 255],
    [255, 255, 255, 255],
  ];
  return createPixels((x, y) => colors[(x >= 4 ? 1 : 0) + (y >= 4 ? 2 : 0)]);
}

function createPixels(readPixel) {
  const pixels = new Uint8Array(8 * 8 * 4);
  for (let y = 0; y < 8; y += 1) {
    for (let x = 0; x < 8; x += 1) {
      pixels.set(readPixel(x, y), (y * 8 + x) * 4);
    }
  }
  return pixels;
}

function encodeNormal(value) {
  return Math.round((value * 0.5 + 0.5) * 255);
}

function findMaterial(document, name) {
  const material = document.materials.find((candidate) => candidate.name === name);
  assert.ok(material, `Missing exported material '${name}'.`);
  return material;
}

function stableUuid(index) {
  return `80000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`;
}

import { mkdir, writeFile } from "node:fs/promises";
import {
  AnimationClip,
  DoubleSide,
  Mesh,
  MeshStandardMaterial,
  PlaneGeometry,
  Scene,
  VectorKeyframeTrack,
} from "three";
import { exportThreeUnityJson, validateDocument } from "../dist/index.js";

const scene = new Scene();
scene.name = "Morph Target Animation";
scene.uuid = "30000000-0000-4000-8000-000000000001";

const geometry = new PlaneGeometry(4, 4, 20, 20);
geometry.name = "Morph Grid";
geometry.uuid = "30000000-0000-4000-8000-000000000002";

const bulgeGeometry = geometry.clone();
const bulgePositions = bulgeGeometry.getAttribute("position");
for (let vertex = 0; vertex < bulgePositions.count; vertex += 1) {
  const x = bulgePositions.getX(vertex);
  const y = bulgePositions.getY(vertex);
  const radiusSquared = x * x + y * y;
  bulgePositions.setZ(vertex, 1.15 * Math.exp(-0.7 * radiusSquared));
}
bulgeGeometry.computeVertexNormals();
const bulgePositionTarget = bulgeGeometry.getAttribute("position");
const bulgeNormalTarget = bulgeGeometry.getAttribute("normal");
bulgePositionTarget.name = "Bulge";
bulgeNormalTarget.name = "Bulge";

const twistGeometry = geometry.clone();
const twistPositions = twistGeometry.getAttribute("position");
for (let vertex = 0; vertex < twistPositions.count; vertex += 1) {
  const x = twistPositions.getX(vertex);
  const y = twistPositions.getY(vertex);
  const angle = y * 0.22;
  const cosine = Math.cos(angle);
  const sine = Math.sin(angle);
  twistPositions.setXYZ(
    vertex,
    x * cosine - y * sine * 0.18,
    y + x * sine * 0.18,
    0.42 * Math.sin(x * 1.35) * Math.cos(y * 0.7),
  );
}
twistGeometry.computeVertexNormals();
const twistPositionTarget = twistGeometry.getAttribute("position");
const twistNormalTarget = twistGeometry.getAttribute("normal");
twistPositionTarget.name = "Twist";
twistNormalTarget.name = "Twist";

geometry.morphAttributes.position = [bulgePositionTarget, twistPositionTarget];
geometry.morphAttributes.normal = [bulgeNormalTarget, twistNormalTarget];
geometry.morphTargetsRelative = false;

const material = new MeshStandardMaterial({ color: 0x3ba7e8, metalness: 0.05, roughness: 0.45, side: DoubleSide });
material.name = "Morph Grid Material";
material.uuid = "30000000-0000-4000-8000-000000000003";
const mesh = new Mesh(geometry, material);
mesh.name = "Animated Morph Grid";
mesh.uuid = "30000000-0000-4000-8000-000000000004";
mesh.morphTargetInfluences = [0.25, 0];
scene.add(mesh);

const clip = new AnimationClip("Morph Cycle", 2, [
  new VectorKeyframeTrack(`${mesh.uuid}.morphTargetInfluences`, [0, 1, 2], [
    0.25, 0,
    0.75, 1,
    0.25, 0,
  ]),
]);
clip.uuid = "30000000-0000-4000-8000-000000000005";
scene.animations.push(clip);

const json = await exportThreeUnityJson(scene, {
  defaultAnimation: clip,
  autoplayAnimation: true,
  animationLoop: true,
  animationSampleRate: 30,
});
const document = JSON.parse(json);
const validation = validateDocument(document);
if (!validation.valid) throw new Error(`Generated morph example is invalid: ${validation.errors.join(" ")}`);

await mkdir(new URL("./output/", import.meta.url), { recursive: true });
const output = new URL("./output/morph-target-animation.threeunity", import.meta.url);
await writeFile(output, `${json}\n`, "utf8");
console.log(`Wrote ${output.pathname}`);

import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import {
  AnimationClip,
  BufferGeometry,
  ClampToEdgeWrapping,
  Color,
  ColorKeyframeTrack,
  DataTexture,
  Float32BufferAttribute,
  Line,
  LineBasicMaterial,
  LineLoop,
  LineSegments,
  NearestFilter,
  NumberKeyframeTrack,
  PerspectiveCamera,
  Points,
  PointsMaterial,
  Quaternion,
  QuaternionKeyframeTrack,
  RGBAFormat,
  Scene,
  Sprite,
  SpriteMaterial,
  SRGBColorSpace,
  UnsignedByteType,
  Vector3,
  VectorKeyframeTrack,
} from "three";
import { exportThreeUnityJson, validateDocument } from "../dist/index.js";

const scene = new Scene();
scene.name = "Line Points Sprite";
scene.uuid = "50000000-0000-4000-8000-000000000001";

const camera = new PerspectiveCamera(48, 16 / 9, 0.1, 100);
camera.name = "Primitive Sample Camera";
camera.uuid = "50000000-0000-4000-8000-000000000002";
camera.position.set(0, 2.5, 16);
camera.lookAt(0, 0.45, 0);
scene.add(camera);

// A small asymmetric arrow/checker texture makes UV orientation, sprite rotation,
// point texturing, and the non-centered sprite pivot easy to distinguish.
const markerPixels = new Uint8Array([
  255, 64, 32, 255, 255, 196, 32, 255, 0, 0, 0, 0, 48, 112, 255, 255,
  0, 0, 0, 0, 255, 224, 64, 255, 48, 112, 255, 255, 48, 112, 255, 255,
  0, 0, 0, 0, 255, 224, 64, 255, 255, 224, 64, 255, 0, 0, 0, 0,
  32, 220, 160, 255, 0, 0, 0, 0, 255, 64, 32, 255, 0, 0, 0, 0,
]);
const markerTexture = new DataTexture(markerPixels, 4, 4, RGBAFormat, UnsignedByteType);
markerTexture.name = "Asymmetric Primitive Marker";
markerTexture.uuid = "50000000-0000-4000-8000-000000000003";
markerTexture.colorSpace = SRGBColorSpace;
markerTexture.wrapS = ClampToEdgeWrapping;
markerTexture.wrapT = ClampToEdgeWrapping;
markerTexture.magFilter = NearestFilter;
markerTexture.minFilter = NearestFilter;
markerTexture.needsUpdate = true;

const continuousGeometry = new BufferGeometry();
continuousGeometry.name = "Bent Polyline Geometry";
continuousGeometry.uuid = "50000000-0000-4000-8000-000000000004";
continuousGeometry.setAttribute("position", new Float32BufferAttribute([
  -1.45, -0.45, 0,
  -1.05, 0.45, 0.15,
  -0.4, -0.05, -0.25,
  0.05, 0.75, 0.1,
  0.6, 0.05, -0.15,
  1.35, 0.55, 0,
], 3));
const continuousMaterial = new LineBasicMaterial({ color: 0x45e6ff });
continuousMaterial.name = "Cyan Line Material";
continuousMaterial.uuid = "50000000-0000-4000-8000-000000000005";
const continuousLine = new Line(continuousGeometry, continuousMaterial);
continuousLine.name = "Continuous Line";
continuousLine.uuid = "50000000-0000-4000-8000-000000000006";
continuousLine.position.set(-4.2, 0.65, 0);
continuousLine.rotation.z = -0.14;
scene.add(continuousLine);

const segmentsGeometry = new BufferGeometry();
segmentsGeometry.name = "Independent Segment Geometry";
segmentsGeometry.uuid = "50000000-0000-4000-8000-000000000007";
segmentsGeometry.setAttribute("position", new Float32BufferAttribute([
  -1.3, -0.55, 0, -0.8, 0.6, 0,
  -0.45, -0.4, 0, 0.25, -0.4, 0,
  0.45, -0.55, 0, 0.95, 0.6, 0,
  1.15, -0.15, 0, 1.45, 0.25, 0,
], 3));
const segmentsMaterial = new LineBasicMaterial({ color: 0xff6a5c });
segmentsMaterial.name = "Coral Segment Material";
segmentsMaterial.uuid = "50000000-0000-4000-8000-000000000008";
const lineSegments = new LineSegments(segmentsGeometry, segmentsMaterial);
lineSegments.name = "Line Segments";
lineSegments.uuid = "50000000-0000-4000-8000-000000000009";
lineSegments.position.set(0, 0.65, 0);
scene.add(lineSegments);

const loopGeometry = new BufferGeometry();
loopGeometry.name = "Pentagon Loop Geometry";
loopGeometry.uuid = "50000000-0000-4000-8000-000000000010";
loopGeometry.setAttribute("position", new Float32BufferAttribute([
  0, 0.9, 0,
  0.95, 0.25, 0,
  0.58, -0.85, 0,
  -0.62, -0.72, 0,
  -1, 0.3, 0,
], 3));
const loopMaterial = new LineBasicMaterial({ color: 0xb7f34a });
loopMaterial.name = "Lime Loop Material";
loopMaterial.uuid = "50000000-0000-4000-8000-000000000011";
const lineLoop = new LineLoop(loopGeometry, loopMaterial);
lineLoop.name = "Line Loop";
lineLoop.uuid = "50000000-0000-4000-8000-000000000012";
lineLoop.position.set(4.1, 0.65, 0);
scene.add(lineLoop);

const pointCount = 30;
const pointPositions = [];
const pointColors = [];
const pointColor = new Color();
for (let point = 0; point < pointCount; point += 1) {
  const localPoint = point % 15;
  const column = localPoint % 5;
  const row = Math.floor(localPoint / 5);
  const farOffset = point < 15 ? 0 : 0.28;
  pointPositions.push(
    (column - 2) * 0.82 + farOffset,
    (row - 1) * 0.64 + farOffset,
    point < 15 ? 2.6 : -3.8,
  );
  pointColor.setHSL(point / pointCount, 0.82, 0.58);
  pointColors.push(pointColor.r, pointColor.g, pointColor.b);
}

const pointsGeometry = new BufferGeometry();
pointsGeometry.name = "Grouped Deterministic Point Cloud";
pointsGeometry.uuid = "50000000-0000-4000-8000-000000000013";
pointsGeometry.setAttribute("position", new Float32BufferAttribute(pointPositions, 3));
pointsGeometry.setAttribute("color", new Float32BufferAttribute(pointColors, 3));
pointsGeometry.setIndex(Array.from({ length: pointCount }, (_, index) => index));
pointsGeometry.addGroup(0, 15, 0);
pointsGeometry.addGroup(15, 15, 1);

const nearPointsMaterial = new PointsMaterial({
  color: 0xffffff,
  map: markerTexture,
  size: 28,
  sizeAttenuation: true,
  transparent: true,
  alphaTest: 0.12,
  vertexColors: true,
});
nearPointsMaterial.name = "Near Point Marker Material";
nearPointsMaterial.uuid = "50000000-0000-4000-8000-000000000014";
const farPointsMaterial = new PointsMaterial({
  color: 0x9ad9ff,
  map: markerTexture,
  size: 28,
  sizeAttenuation: true,
  transparent: true,
  alphaTest: 0.12,
  vertexColors: true,
});
farPointsMaterial.name = "Far Point Marker Material";
farPointsMaterial.uuid = "50000000-0000-4000-8000-000000000015";
const points = new Points(pointsGeometry, [nearPointsMaterial, farPointsMaterial]);
points.name = "Points Cloud";
points.uuid = "50000000-0000-4000-8000-000000000016";
points.position.set(0, -1.9, 0);
scene.add(points);

const centerSpriteMaterial = new SpriteMaterial({
  color: 0xffffff,
  map: markerTexture,
  rotation: Math.PI / 8,
  sizeAttenuation: true,
  transparent: true,
  alphaTest: 0.12,
});
centerSpriteMaterial.name = "Centered Attenuated Sprite Material";
centerSpriteMaterial.uuid = "50000000-0000-4000-8000-000000000017";
const centerSprite = new Sprite(centerSpriteMaterial);
centerSprite.name = "Center Sprite";
centerSprite.uuid = "50000000-0000-4000-8000-000000000018";
centerSprite.center.set(0.5, 0.5);
centerSprite.position.set(-2.25, 3.05, 0.4);
centerSprite.scale.set(2.2, 1.55, 1);
scene.add(centerSprite);

const pivotSpriteMaterial = new SpriteMaterial({
  color: 0x8fffe1,
  map: markerTexture,
  rotation: -Math.PI / 5,
  sizeAttenuation: false,
  transparent: true,
  alphaTest: 0.12,
});
pivotSpriteMaterial.name = "Corner Fixed-Size Sprite Material";
pivotSpriteMaterial.uuid = "50000000-0000-4000-8000-000000000019";
const pivotSprite = new Sprite(pivotSpriteMaterial);
pivotSprite.name = "Pivot Sprite";
pivotSprite.uuid = "50000000-0000-4000-8000-000000000020";
pivotSprite.center.set(0, 0);
pivotSprite.position.set(1.45, 2.35, -0.8);
pivotSprite.scale.set(0.18, 0.28, 1);
scene.add(pivotSprite);

const startRotation = new Quaternion().setFromAxisAngle(new Vector3(0, 0, 1), -0.14);
const endRotation = new Quaternion().setFromAxisAngle(new Vector3(0, 0, 1), 0.3);
const duration = 4;
const times = [0, duration / 2, duration];
const clip = new AnimationClip("Primitive Motion and Color", duration, [
  new QuaternionKeyframeTrack(`${continuousLine.uuid}.quaternion`, times, [
    ...startRotation.toArray(),
    ...endRotation.toArray(),
    ...startRotation.toArray(),
  ]),
  new VectorKeyframeTrack(`${points.uuid}.position`, times, [
    0, -1.9, 0,
    0, -1.3, 0,
    0, -1.9, 0,
  ]),
  new VectorKeyframeTrack(`${centerSprite.uuid}.scale`, times, [
    2.2, 1.55, 1,
    2.7, 1.2, 1,
    2.2, 1.55, 1,
  ]),
  new ColorKeyframeTrack(`${centerSprite.uuid}.material.color`, times, [
    1, 1, 1,
    1, 0.35, 0.18,
    1, 1, 1,
  ]),
  new NumberKeyframeTrack(`${centerSprite.uuid}.material.opacity`, times, [1, 0.45, 1]),
]);
clip.name = "Primitive Motion and Color";
clip.uuid = "50000000-0000-4000-8000-000000000021";
scene.animations.push(clip);

const json = await exportThreeUnityJson(scene, {
  defaultAnimation: clip,
  autoplayAnimation: true,
  animationLoop: true,
  animationSampleRate: 30,
});
const document = JSON.parse(json);
const validation = validateDocument(document);
if (!validation.valid) throw new Error(`Generated primitive example is invalid: ${validation.errors.join(" ")}`);

assert.equal(document.version, 5);
assert.deepEqual(
  document.primitives.map((primitive) => primitive.type).sort(),
  ["line", "line-loop", "line-segments", "points", "sprite", "sprite"],
);
assert.equal(document.primitives.find((primitive) => primitive.type === "points")?.groups.length, 2);
assert.ok(document.animations[0]?.tracks.some((track) => track.property === "materialBaseColor"));

await mkdir(new URL("./output/", import.meta.url), { recursive: true });
const output = new URL("./output/non-mesh-primitives.threeunity", import.meta.url);
await writeFile(output, `${json}\n`, "utf8");
console.log(`Wrote ${output.pathname}`);

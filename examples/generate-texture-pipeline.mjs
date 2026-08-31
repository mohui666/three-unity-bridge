import assert from "node:assert/strict";
import { once } from "node:events";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { fileURLToPath } from "node:url";
import {
  ClampToEdgeWrapping,
  DataTexture,
  FloatType,
  HalfFloatType,
  LinearFilter,
  LinearMipmapLinearFilter,
  Mesh,
  MeshBasicMaterial,
  MirroredRepeatWrapping,
  NearestFilter,
  NearestMipmapNearestFilter,
  NoColorSpace,
  PerspectiveCamera,
  PlaneGeometry,
  RedFormat,
  RepeatWrapping,
  RGBAFormat,
  RGFormat,
  RGBFormat,
  Scene,
  SRGBColorSpace,
  Texture,
  UnsignedByteType,
} from "three";
import { exportThreeUnity, validateDocument } from "../dist/index.js";
import { createNodeTextureResolver } from "../dist/node.js";

// Both fixed 8x8 images use top-left red, top-right green, bottom-left blue,
// and bottom-right yellow so the importer can assert orientation by corner.
const ASYMMETRIC_PNG_BASE64 =
  "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAeSURBVChTY/jPwPAfGWOBNFeAJv///38QgcB0UAAAN5uPcRzZBK4AAAAASUVORK5CYII=";
const ASYMMETRIC_JPEG_BASE64 =
  "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAIBAQIBAQICAgICAgICAwUDAwMDAwYEBAMFBwYHBwcGBwcICQsJCAgKCAcHCg0KCgsMDAwMBwkODw0MDgsMDAz/2wBDAQICAgMDAwYDAwYMCAcIDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAz/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDA/wCCCngr/hsr/ha3+k/8I5/wjn9kf8s/tn2jz/t3vHt2+T753dsclFFf56ftAsHR4E8fM+4V4VX1fBYf6r7OnrPl58Fhqkveqc83ec5S96TteyskkvO4j8M+G/EzManG3G2G+tZjire1q89SnzeziqMPcozp048tOnCPuwV7Xd5Nt//Z";

const examplesDirectory = new URL("./", import.meta.url);
const outputDirectory = new URL("./output/", import.meta.url);
const fixturesDirectory = new URL("./output/texture-source-fixtures/", import.meta.url);
const localSourceUri = "./output/texture-source-fixtures/asymmetric.png";
const output = new URL("./output/texture-pipeline-v7.threeunity", import.meta.url);
const pngBytes = Buffer.from(ASYMMETRIC_PNG_BASE64, "base64");
const jpegBytes = Buffer.from(ASYMMETRIC_JPEG_BASE64, "base64");

await mkdir(fixturesDirectory, { recursive: true });
await writeFile(new URL("./asymmetric.png", fixturesDirectory), pngBytes);
await writeFile(new URL("./asymmetric.jpg", fixturesDirectory), jpegBytes);

const server = createServer((request, response) => {
  if (request.url !== "/asymmetric.jpg") {
    response.writeHead(404).end();
    return;
  }
  response.writeHead(200, {
    "Content-Length": jpegBytes.length,
    "Content-Type": "image/jpeg",
  });
  response.end(jpegBytes);
});
server.listen(0, "127.0.0.1");
await once(server, "listening");

const address = server.address();
assert.ok(address && typeof address === "object");
const httpSourceUri = `http://127.0.0.1:${address.port}/asymmetric.jpg`;

let document;
try {
  const textures = createTextures(httpSourceUri);
  const scene = createScene(textures);
  document = await exportThreeUnity(scene, {
    textureResolver: createNodeTextureResolver({ baseDirectory: examplesDirectory }),
  });
} finally {
  await new Promise((resolve, reject) => {
    server.close((error) => error ? reject(error) : resolve());
  });
  await rm(fixturesDirectory, { recursive: true, force: true });
}

assert.equal(server.listening, false);
const validation = validateDocument(document);
if (!validation.valid) throw new Error(`Generated texture example is invalid: ${validation.errors.join(" ")}`);

const encodedTextures = document.textures.filter((texture) => texture.encoding === "encoded-image");
const rawTextures = document.textures.filter((texture) => texture.encoding === "raw");
assert.equal(document.version, 7);
assert.equal(encodedTextures.length, 2);
assert.equal(rawTextures.length, 5);
assert.ok(document.textures.every((texture) => texture.data.length > 0));

const json = `${JSON.stringify(document, null, 2)}\n`;
const examplesPath = fileURLToPath(examplesDirectory);
assert.equal(json.includes(localSourceUri), false);
assert.equal(json.includes(httpSourceUri), false);
assert.equal(json.includes("http://"), false);
assert.equal(json.includes("https://"), false);
assert.equal(json.includes("file://"), false);
assert.equal(json.includes("texture-source-fixtures"), false);
assert.equal(json.includes(examplesPath), false);
assert.equal(json.includes(examplesPath.replaceAll("\\", "\\\\")), false);
assert.equal(json.includes(examplesPath.replaceAll("\\", "/")), false);

await mkdir(outputDirectory, { recursive: true });
await writeFile(output, json, "utf8");
const persistedDocument = JSON.parse(await readFile(output, "utf8"));
const persistedValidation = validateDocument(persistedDocument);
if (!persistedValidation.valid) {
  throw new Error(`Persisted texture example is invalid: ${persistedValidation.errors.join(" ")}`);
}

console.log("format: 7");
console.log("encoded images: 2");
console.log("raw textures: 5");
console.log("local source: embedded");
console.log("http source: embedded");
console.log("source server: closed");
console.log("source URIs persisted: no");
console.log(`Wrote ${output.pathname}`);

function createTextures(httpSourceUri) {
  const localPng = new Texture();
  localPng.name = "Local Asymmetric PNG";
  localPng.uuid = stableUuid(1);
  localPng.userData.threeUnitySource = localSourceUri;
  localPng.flipY = true;
  localPng.colorSpace = SRGBColorSpace;
  localPng.wrapS = RepeatWrapping;
  localPng.wrapT = ClampToEdgeWrapping;
  localPng.magFilter = LinearFilter;
  localPng.minFilter = LinearMipmapLinearFilter;
  localPng.generateMipmaps = true;
  localPng.anisotropy = 4;
  localPng.repeat.set(1.75, 1.25);
  localPng.offset.set(0.08, 0.05);

  const httpJpeg = new Texture();
  httpJpeg.name = "Loopback Asymmetric JPEG";
  httpJpeg.uuid = stableUuid(2);
  httpJpeg.userData.threeUnitySource = httpSourceUri;
  httpJpeg.flipY = true;
  httpJpeg.colorSpace = SRGBColorSpace;
  httpJpeg.wrapS = MirroredRepeatWrapping;
  httpJpeg.wrapT = RepeatWrapping;
  httpJpeg.magFilter = LinearFilter;
  httpJpeg.minFilter = LinearFilter;
  httpJpeg.generateMipmaps = false;
  httpJpeg.anisotropy = 2;
  httpJpeg.repeat.set(1.75, 1.25);

  const r8 = configureRawTexture(
    new DataTexture(createR8Pixels(), 4, 4, RedFormat, UnsignedByteType),
    3,
    "Uint8 R",
    {
      wrapS: ClampToEdgeWrapping,
      wrapT: ClampToEdgeWrapping,
      magFilter: NearestFilter,
      minFilter: NearestFilter,
      generateMipmaps: false,
      anisotropy: 1,
    },
  );
  const rg8 = configureRawTexture(
    new DataTexture(createRg8Pixels(), 4, 4, RGFormat, UnsignedByteType),
    4,
    "Uint8 RG",
    {
      wrapS: RepeatWrapping,
      wrapT: MirroredRepeatWrapping,
      magFilter: LinearFilter,
      minFilter: LinearFilter,
      generateMipmaps: false,
      anisotropy: 2,
    },
  );
  const rgba8 = configureRawTexture(
    new DataTexture(createRgba8Pixels(), 4, 4, RGBAFormat, UnsignedByteType),
    5,
    "Uint8 RGBA",
    {
      wrapS: MirroredRepeatWrapping,
      wrapT: ClampToEdgeWrapping,
      magFilter: NearestFilter,
      minFilter: NearestMipmapNearestFilter,
      generateMipmaps: true,
      anisotropy: 3,
    },
  );
  const rgbHalf = configureRawTexture(
    new DataTexture(createRgbHalfPixels(), 4, 4, RGBFormat, HalfFloatType),
    6,
    "HalfFloat RGB",
    {
      wrapS: RepeatWrapping,
      wrapT: RepeatWrapping,
      magFilter: LinearFilter,
      minFilter: LinearMipmapLinearFilter,
      generateMipmaps: true,
      anisotropy: 8,
    },
  );
  const rgbaFloat = configureRawTexture(
    new DataTexture(createRgbaFloatPixels(), 4, 4, RGBAFormat, FloatType),
    7,
    "Float32 RGBA",
    {
      wrapS: ClampToEdgeWrapping,
      wrapT: MirroredRepeatWrapping,
      magFilter: LinearFilter,
      minFilter: LinearFilter,
      generateMipmaps: false,
      anisotropy: 4,
    },
  );

  return [localPng, httpJpeg, r8, rg8, rgba8, rgbHalf, rgbaFloat];
}

function configureRawTexture(texture, uuidIndex, name, sampler) {
  texture.name = name;
  texture.uuid = stableUuid(uuidIndex);
  texture.flipY = false;
  texture.colorSpace = NoColorSpace;
  texture.wrapS = sampler.wrapS;
  texture.wrapT = sampler.wrapT;
  texture.magFilter = sampler.magFilter;
  texture.minFilter = sampler.minFilter;
  texture.generateMipmaps = sampler.generateMipmaps;
  texture.anisotropy = sampler.anisotropy;
  texture.needsUpdate = true;
  return texture;
}

function createScene(textures) {
  const scene = new Scene();
  scene.name = "Texture Sources and DataTexture";
  scene.uuid = stableUuid(20);

  const camera = new PerspectiveCamera(45, 16 / 9, 0.1, 100);
  camera.name = "Texture Pipeline Camera";
  camera.uuid = stableUuid(21);
  camera.position.set(0, 0, 13);
  camera.lookAt(0, 0, 0);
  scene.add(camera);

  const positions = [
    [-5.4, 1.7],
    [-1.8, 1.7],
    [1.8, 1.7],
    [5.4, 1.7],
    [-5.4, -1.7],
    [-1.8, -1.7],
    [1.8, -1.7],
    [5.4, -1.7],
  ];
  const panels = [
    ...textures.map((texture) => ({ label: texture.name, texture })),
    { label: "Local PNG Shared Reference", texture: textures[0] },
  ];
  panels.forEach(({ label, texture }, index) => {
    const geometry = new PlaneGeometry(2.5, 2.5);
    geometry.name = `${label} Panel Geometry`;
    geometry.uuid = stableUuid(30 + index * 3);
    const material = new MeshBasicMaterial({ map: texture });
    material.name = `${label} Panel Material`;
    material.uuid = stableUuid(31 + index * 3);
    const panel = new Mesh(geometry, material);
    panel.name = `${label} Panel`;
    panel.uuid = stableUuid(32 + index * 3);
    panel.position.set(positions[index][0], positions[index][1], 0);
    scene.add(panel);
  });

  return scene;
}

function createR8Pixels() {
  return Uint8Array.from({ length: 16 }, (_, index) => Math.round((index % 4) * 255 / 3));
}

function createRg8Pixels() {
  const pixels = new Uint8Array(4 * 4 * 2);
  for (let y = 0; y < 4; y += 1) {
    for (let x = 0; x < 4; x += 1) {
      const offset = (y * 4 + x) * 2;
      pixels[offset] = Math.round(x * 255 / 3);
      pixels[offset + 1] = Math.round(y * 255 / 3);
    }
  }
  return pixels;
}

function createRgba8Pixels() {
  const quadrants = [
    [255, 48, 32, 255],
    [48, 255, 64, 255],
    [48, 96, 255, 255],
    [255, 224, 32, 255],
  ];
  const pixels = new Uint8Array(4 * 4 * 4);
  for (let y = 0; y < 4; y += 1) {
    for (let x = 0; x < 4; x += 1) {
      const quadrant = (y < 2 ? 0 : 2) + (x < 2 ? 0 : 1);
      pixels.set(quadrants[quadrant], (y * 4 + x) * 4);
    }
  }
  return pixels;
}

function createRgbHalfPixels() {
  const halfZero = 0x0000;
  const halfOne = 0x3c00;
  const quadrants = [
    [halfOne, halfZero, halfZero],
    [halfZero, halfOne, halfZero],
    [halfZero, halfZero, halfOne],
    [halfOne, halfOne, halfZero],
  ];
  const pixels = new Uint16Array(4 * 4 * 3);
  for (let y = 0; y < 4; y += 1) {
    for (let x = 0; x < 4; x += 1) {
      const quadrant = (y < 2 ? 0 : 2) + (x < 2 ? 0 : 1);
      pixels.set(quadrants[quadrant], (y * 4 + x) * 3);
    }
  }
  return pixels;
}

function createRgbaFloatPixels() {
  const pixels = new Float32Array(4 * 4 * 4);
  for (let y = 0; y < 4; y += 1) {
    for (let x = 0; x < 4; x += 1) {
      const offset = (y * 4 + x) * 4;
      pixels[offset] = x / 3;
      pixels[offset + 1] = y / 3;
      pixels[offset + 2] = (x + y) % 2;
      pixels[offset + 3] = 1;
    }
  }
  return pixels;
}

function stableUuid(index) {
  return `70000000-0000-4000-8000-${String(index).padStart(12, "0")}`;
}

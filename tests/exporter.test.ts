import assert from "node:assert/strict";
import { once } from "node:events";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  AnimationClip,
  Bone,
  BoxGeometry,
  BufferGeometry,
  Color,
  ColorKeyframeTrack,
  DataTexture,
  DirectionalLight,
  FloatType,
  Float32BufferAttribute,
  HalfFloatType,
  InterleavedBuffer,
  InterleavedBufferAttribute,
  InstancedMesh,
  Line,
  LineBasicMaterial,
  LinearFilter,
  LinearMipmapLinearFilter,
  LinearSRGBColorSpace,
  LineLoop,
  LineSegments,
  Matrix4,
  Mesh,
  MeshLambertMaterial,
  MeshStandardMaterial,
  MirroredRepeatWrapping,
  NumberKeyframeTrack,
  NoColorSpace,
  PerspectiveCamera,
  Points,
  PointsMaterial,
  Quaternion,
  QuaternionKeyframeTrack,
  RedFormat,
  RepeatWrapping,
  RGFormat,
  RGBFormat,
  RGBAFormat,
  Scene,
  Skeleton,
  SkinnedMesh,
  SRGBColorSpace,
  Sprite,
  SpriteMaterial,
  Texture,
  Uint16BufferAttribute,
  UnsignedByteType,
  Vector3,
  VectorKeyframeTrack,
} from "three";
import { exportThreeUnity, validateDocument } from "../src/index.js";
import { createNodeTextureResolver } from "../src/node.js";

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
  assert.equal(document.version, 8);
  assert.equal(document.textures[0].encoding, "raw");
  assert.equal(document.textures[0].mimeType, "");
  assert.equal(document.textures[0].pixelFormat, "rgba");
  assert.equal(document.textures[0].componentType, "uint8");
  assert.equal(document.textures[0].colorSpace, "srgb");
  assert.equal(document.textures[0].filterMode, "point");
  assert.equal(document.textures[0].mipmaps, false);
  assert.equal(document.textures[0].anisotropy, 1);
  assert.equal(document.textures[0].data, "/wAA/w==");

  const cubeNode = document.nodes.find((node) => node.name === "Exported Cube");
  assert.ok(cubeNode);
  assert.equal(cubeNode.parentId, document.nodes[0].id);
  assert.deepEqual(cubeNode.position, [4, 5, 6]);
  assert.equal(cubeNode.components[0].type, "Spin");
  assert.equal(cubeNode.components[0].dataJson, "{\"degreesPerSecond\":30}");
  assert.equal(cubeNode.metadataJson, "{\"gameplayTag\":\"pickup\"}");
});

test("exports one self-contained v7 pipeline for local/HTTP images and the raw DataTexture matrix", async () => {
  const directory = await mkdtemp(join(tmpdir(), "three-unity-textures-"));
  const pngBytes = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
    "base64",
  );
  const jpegBytes = Uint8Array.from([0xff, 0xd8, 0xff, 0xd9]);
  await writeFile(join(directory, "local.png"), pngBytes);

  const server = createServer((request, response) => {
    if (request.url !== "/remote.jpg") {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { "Content-Type": "image/jpeg" });
    response.end(jpegBytes);
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const httpUri = `http://127.0.0.1:${address.port}/remote.jpg`;

  try {
    const localTexture = new Texture();
    localTexture.name = "Local PNG";
    localTexture.userData.threeUnitySource = "./local.png";
    localTexture.flipY = true;
    localTexture.colorSpace = SRGBColorSpace;
    localTexture.wrapS = RepeatWrapping;
    localTexture.wrapT = MirroredRepeatWrapping;
    localTexture.magFilter = LinearFilter;
    localTexture.minFilter = LinearMipmapLinearFilter;
    localTexture.generateMipmaps = true;
    localTexture.anisotropy = 4;

    const httpTexture = new Texture();
    httpTexture.name = "HTTP JPEG";
    httpTexture.image = { currentSrc: httpUri };

    const r8Data = new Uint8Array([0x12, 0xfe]);
    const rg8Data = new Uint8ClampedArray([0x01, 0x02, 0x80, 0xff]);
    const rgbHalfData = new Uint16Array([0x3c00, 0x3800, 0x0000]);
    const rgbaFloatData = new Float32Array([1, 0.5, 0.25, 1]);
    const r8 = new DataTexture(r8Data, 2, 1, RedFormat, UnsignedByteType);
    const rg8 = new DataTexture(rg8Data, 2, 1, RGFormat, UnsignedByteType);
    const rgbHalf = new DataTexture(rgbHalfData, 1, 1, RGBFormat, HalfFloatType);
    const rgbaFloat = new DataTexture(rgbaFloatData, 1, 1, RGBAFormat, FloatType);
    r8.name = "R8";
    rg8.name = "RG8";
    rgbHalf.name = "RGB Half";
    rgbaFloat.name = "RGBA Float";
    rgbHalf.magFilter = LinearFilter;
    rgbHalf.minFilter = LinearMipmapLinearFilter;
    rgbHalf.generateMipmaps = true;
    rgbHalf.anisotropy = 8;
    rgbaFloat.magFilter = LinearFilter;
    rgbaFloat.minFilter = LinearFilter;
    rgbaFloat.generateMipmaps = false;
    rgbaFloat.flipY = false;

    const localMaterial = new MeshStandardMaterial({ map: localTexture });
    localMaterial.name = "Shared Local Material";
    const localMesh = new Mesh(new BoxGeometry(), localMaterial);
    const localInstances = new InstancedMesh(new BoxGeometry(), localMaterial, 1);
    localInstances.setMatrixAt(0, new Matrix4());
    const localSpriteMaterial = new SpriteMaterial({ map: localTexture });
    localSpriteMaterial.name = "Shared Local Sprite Material";
    const scene = new Scene();
    scene.add(
      localMesh,
      localInstances,
      new Sprite(localSpriteMaterial),
      new Mesh(new BoxGeometry(), new MeshStandardMaterial({ map: httpTexture })),
      new Mesh(new BoxGeometry(), new MeshStandardMaterial({ map: r8 })),
      new Mesh(new BoxGeometry(), new MeshStandardMaterial({ map: rg8 })),
      new Mesh(new BoxGeometry(), new MeshStandardMaterial({ map: rgbHalf })),
      new Mesh(new BoxGeometry(), new MeshStandardMaterial({ map: rgbaFloat })),
    );

    const sourceState = [r8, rg8, rgbHalf, rgbaFloat].map((texture) => ({
      data: Array.from(texture.image.data),
      format: texture.format,
      type: texture.type,
      flipY: texture.flipY,
      colorSpace: texture.colorSpace,
      generateMipmaps: texture.generateMipmaps,
      magFilter: texture.magFilter,
      minFilter: texture.minFilter,
      anisotropy: texture.anisotropy,
      unpackAlignment: texture.unpackAlignment,
      version: texture.version,
    }));
    const encodedSourceState = [localTexture, httpTexture].map((texture) => ({
      source: texture.source,
      sourceData: texture.source.data,
      image: texture.image,
      explicitSource: texture.userData.threeUnitySource,
      flipY: texture.flipY,
      colorSpace: texture.colorSpace,
      wrapS: texture.wrapS,
      wrapT: texture.wrapT,
      magFilter: texture.magFilter,
      minFilter: texture.minFilter,
      generateMipmaps: texture.generateMipmaps,
      anisotropy: texture.anisotropy,
      version: texture.version,
    }));
    const resolutionCounts = new Map<string, number>();
    const nodeResolver = createNodeTextureResolver({ baseDirectory: directory });
    const document = await exportThreeUnity(scene, {
      textureResolver: async (request) => {
        resolutionCounts.set(request.sourceUri, (resolutionCounts.get(request.sourceUri) ?? 0) + 1);
        return nodeResolver(request);
      },
    });

    assert.equal(document.version, 8);
    assert.deepEqual(validateDocument(document), { valid: true, errors: [] });
    assert.equal(resolutionCounts.get("./local.png"), 1);
    assert.equal(resolutionCounts.get(httpUri), 1);
    const json = JSON.stringify(document);
    assert.equal(json.includes("./local.png"), false);
    assert.equal(json.includes(httpUri), false);
    assert.equal(json.includes(directory), false);

    const localRecord = document.textures.find((texture) => texture.name === localTexture.name);
    const httpRecord = document.textures.find((texture) => texture.name === httpTexture.name);
    assert.ok(localRecord && httpRecord);
    assert.equal(localRecord.encoding, "encoded-image");
    assert.equal(localRecord.mimeType, "image/png");
    assert.deepEqual([...Buffer.from(localRecord.data, "base64").subarray(0, 8)], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    assert.equal(localRecord.colorSpace, "srgb");
    assert.equal(localRecord.flipY, true);
    assert.equal(localRecord.wrapS, "repeat");
    assert.equal(localRecord.wrapT, "mirror");
    assert.equal(localRecord.filterMode, "trilinear");
    assert.equal(localRecord.mipmaps, true);
    assert.equal(localRecord.anisotropy, 4);
    assert.equal(httpRecord.mimeType, "image/jpeg");
    assert.deepEqual([...Buffer.from(httpRecord.data, "base64")], [...jpegBytes]);

    const rawByName = new Map(document.textures.filter((texture) => texture.encoding === "raw").map((texture) => [texture.name, texture]));
    assert.deepEqual(
      ["R8", "RG8", "RGB Half", "RGBA Float"].map((name) => {
        const texture = rawByName.get(name);
        assert.ok(texture);
        return [texture.pixelFormat, texture.componentType];
      }),
      [["r", "uint8"], ["rg", "uint8"], ["rgb", "float16"], ["rgba", "float32"]],
    );
    assert.deepEqual([...Buffer.from(rawByName.get("R8")!.data, "base64")], [...r8Data]);
    assert.deepEqual([...Buffer.from(rawByName.get("RG8")!.data, "base64")], [...rg8Data]);
    assert.deepEqual([...Buffer.from(rawByName.get("RGB Half")!.data, "base64")], [0x00, 0x3c, 0x00, 0x38, 0x00, 0x00]);
    const floatBytes = Buffer.from(rawByName.get("RGBA Float")!.data, "base64");
    const floatView = new DataView(floatBytes.buffer, floatBytes.byteOffset, floatBytes.byteLength);
    assert.deepEqual([0, 1, 2, 3].map((index) => floatView.getFloat32(index * 4, true)), [...rgbaFloatData]);
    assert.equal(rawByName.get("RGB Half")!.filterMode, "trilinear");
    assert.equal(rawByName.get("RGB Half")!.mipmaps, true);
    assert.equal(rawByName.get("RGB Half")!.anisotropy, 8);

    const localMaterialRecords = document.materials.filter((material) =>
      material.name === localMaterial.name || material.name === localSpriteMaterial.name
    );
    assert.equal(localMaterialRecords.length, 2);
    assert.ok(localMaterialRecords.every((material) => material.baseColorTextureId === localRecord.id));
    const instancedNode = document.nodes.find((node) => node.instancedMeshId);
    assert.ok(instancedNode);
    const instancedRecord = document.instancedMeshes.find((record) => record.id === instancedNode.instancedMeshId);
    assert.ok(instancedRecord);
    const instancedMesh = document.meshes.find((mesh) => mesh.id === instancedRecord.meshId);
    assert.ok(instancedMesh);
    const instancedMaterial = document.materials.find((material) => material.id === instancedMesh.materialIds[0]);
    assert.ok(instancedMaterial);
    assert.equal(instancedMaterial.baseColorTextureId, localRecord.id);

    for (const [index, texture] of [localTexture, httpTexture].entries()) {
      assert.equal(texture.source, encodedSourceState[index].source);
      assert.equal(texture.source.data, encodedSourceState[index].sourceData);
      assert.equal(texture.image, encodedSourceState[index].image);
      assert.equal(texture.userData.threeUnitySource, encodedSourceState[index].explicitSource);
      assert.equal(texture.flipY, encodedSourceState[index].flipY);
      assert.equal(texture.colorSpace, encodedSourceState[index].colorSpace);
      assert.equal(texture.wrapS, encodedSourceState[index].wrapS);
      assert.equal(texture.wrapT, encodedSourceState[index].wrapT);
      assert.equal(texture.magFilter, encodedSourceState[index].magFilter);
      assert.equal(texture.minFilter, encodedSourceState[index].minFilter);
      assert.equal(texture.generateMipmaps, encodedSourceState[index].generateMipmaps);
      assert.equal(texture.anisotropy, encodedSourceState[index].anisotropy);
      assert.equal(texture.version, encodedSourceState[index].version);
    }
    for (const [index, texture] of [r8, rg8, rgbHalf, rgbaFloat].entries()) {
      assert.deepEqual(Array.from(texture.image.data), sourceState[index].data);
      assert.equal(texture.format, sourceState[index].format);
      assert.equal(texture.type, sourceState[index].type);
      assert.equal(texture.flipY, sourceState[index].flipY);
      assert.equal(texture.colorSpace, sourceState[index].colorSpace);
      assert.equal(texture.generateMipmaps, sourceState[index].generateMipmaps);
      assert.equal(texture.magFilter, sourceState[index].magFilter);
      assert.equal(texture.minFilter, sourceState[index].minFilter);
      assert.equal(texture.anisotropy, sourceState[index].anisotropy);
      assert.equal(texture.unpackAlignment, sourceState[index].unpackAlignment);
      assert.equal(texture.version, sourceState[index].version);
    }

    const missingV7Field = structuredClone(document) as unknown as { textures: Array<Record<string, unknown>> };
    delete missingV7Field.textures[0].filterMode;
    assert.match(validateDocument(missingV7Field).errors.join("\n"), /filterMode must be point, bilinear, or trilinear/);
    const wrongRawLength = structuredClone(document);
    const wrongRaw = wrongRawLength.textures.find((texture) => texture.encoding === "raw");
    assert.ok(wrongRaw);
    wrongRaw.data = Buffer.from([0]).toString("base64");
    assert.match(validateDocument(wrongRawLength).errors.join("\n"), /data byte length must be/);
  } finally {
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
    await rm(directory, { recursive: true, force: true });
  }
});

test("exports one format-v8 PBR material-map contract without mutating source state", async () => {
  const makeTexture = (name: string, values: number[], colorSpace: Texture["colorSpace"] = NoColorSpace): DataTexture => {
    const texture = new DataTexture(new Uint8Array(values), 2, 2, RGBAFormat, UnsignedByteType);
    texture.name = name;
    texture.colorSpace = colorSpace;
    texture.wrapS = RepeatWrapping;
    texture.wrapT = MirroredRepeatWrapping;
    texture.magFilter = LinearFilter;
    texture.minFilter = LinearFilter;
    texture.generateMipmaps = false;
    texture.anisotropy = 2;
    return texture;
  };
  const pixels = [
    16, 32, 48, 255,
    64, 80, 96, 255,
    112, 128, 144, 255,
    160, 176, 192, 255,
  ];
  const sharedMask = makeTexture("Shared Metalness Roughness", pixels);
  sharedMask.repeat.set(2, 3);
  sharedMask.offset.set(0.125, 0.25);
  const separateMetalness = makeTexture("Separate Metalness", pixels, LinearSRGBColorSpace);
  const separateRoughness = makeTexture("Separate Roughness", pixels);
  separateMetalness.repeat.set(1.25, 0.75);
  separateRoughness.repeat.copy(separateMetalness.repeat);
  separateMetalness.offset.set(0.05, 0.15);
  separateRoughness.offset.copy(separateMetalness.offset);
  const normalMap = makeTexture("Tangent Normal", [
    128, 128, 255, 255,
    192, 128, 238, 255,
    64, 192, 220, 255,
    128, 64, 246, 255,
  ]);
  normalMap.repeat.set(0.5, 1.5);
  normalMap.offset.set(0.2, 0.3);
  const emissiveMap = makeTexture("Emission Color", pixels, SRGBColorSpace);
  emissiveMap.repeat.set(3, 2);
  emissiveMap.offset.set(0.4, 0.1);

  const sharedMaterial = new MeshStandardMaterial({
    metalness: 0.7,
    roughness: 0.4,
    metalnessMap: sharedMask,
    roughnessMap: sharedMask,
    normalMap,
    emissiveMap,
  });
  sharedMaterial.name = "Shared PBR Material";
  sharedMaterial.normalScale.set(-0.5, 1.25);
  sharedMaterial.emissive.setRGB(0.25, 0.5, 0.75);
  sharedMaterial.emissiveIntensity = 2.5;
  const separateMaterial = new MeshStandardMaterial({
    metalness: 0.3,
    roughness: 0.8,
    metalnessMap: separateMetalness,
    roughnessMap: separateRoughness,
  });
  separateMaterial.name = "Separate PBR Material";

  const tangentValues = [
    1, 0, 0, 1,
    1, 0, 0, -1,
    0, 1, 0, 1,
  ];
  const geometry = new BufferGeometry();
  geometry.setAttribute("position", new Float32BufferAttribute([
    -0.5, 0, 0,
    0.5, 0, 0,
    0, 1, 0,
  ], 3));
  geometry.setAttribute("normal", new Float32BufferAttribute([
    0, 0, 1,
    0, 0, 1,
    0, 0, 1,
  ], 3));
  geometry.setAttribute("uv", new Float32BufferAttribute([0, 0, 1, 0, 0.5, 1], 2));
  geometry.setAttribute("tangent", new Float32BufferAttribute(tangentValues, 4));
  geometry.setIndex([0, 1, 2]);
  const sharedMesh = new Mesh(geometry, sharedMaterial);
  sharedMesh.name = "Shared PBR Mesh";
  const separateMesh = new Mesh(geometry, separateMaterial);
  separateMesh.name = "Separate PBR Mesh";
  separateMesh.position.x = 2;
  const scene = new Scene();
  scene.add(sharedMesh, separateMesh);
  scene.animations.push(new AnimationClip("Emission Intensity Pulse", 1, [
    new NumberKeyframeTrack(`${sharedMesh.uuid}.material.emissiveIntensity`, [0, 1], [2.5, 4.5]),
  ]));

  const sourceTextures = [sharedMask, separateMetalness, separateRoughness, normalMap, emissiveMap];
  const original = {
    emissive: sharedMaterial.emissive.toArray(),
    emissiveIntensity: sharedMaterial.emissiveIntensity,
    metalness: sharedMaterial.metalness,
    roughness: sharedMaterial.roughness,
    tangent: Array.from((geometry.getAttribute("tangent") as Float32BufferAttribute).array),
    textures: sourceTextures.map((texture) => ({
      bytes: Array.from(texture.image.data),
      offset: texture.offset.toArray(),
      repeat: texture.repeat.toArray(),
      colorSpace: texture.colorSpace,
    })),
  };

  const document = await exportThreeUnity(scene, { animationSampleRate: 1 });
  assert.equal(document.version, 8);
  assert.deepEqual(validateDocument(document), { valid: true, errors: [] });

  const exportedShared = document.materials.find((material) => material.name === sharedMaterial.name);
  const exportedSeparate = document.materials.find((material) => material.name === separateMaterial.name);
  assert.ok(exportedShared && exportedSeparate);
  assert.equal(exportedShared.metalnessTextureId, exportedShared.roughnessTextureId);
  assert.equal(exportedShared.metallicRoughnessTextureId, exportedShared.metalnessTextureId);
  assert.notEqual(exportedSeparate.metalnessTextureId, exportedSeparate.roughnessTextureId);
  assert.equal(exportedSeparate.metallicRoughnessTextureId, "");
  assert.deepEqual(exportedShared.metalnessTextureST, [2, 3, 0.125, 0.25]);
  assert.deepEqual(exportedShared.roughnessTextureST, [2, 3, 0.125, 0.25]);
  assert.deepEqual(exportedSeparate.metalnessTextureST, [1.25, 0.75, 0.05, 0.15]);
  assert.deepEqual(exportedSeparate.roughnessTextureST, [1.25, 0.75, 0.05, 0.15]);
  assert.deepEqual(exportedShared.normalTextureST, [0.5, 1.5, 0.2, 0.3]);
  assert.deepEqual(exportedShared.emissiveTextureST, [3, 2, 0.4, 0.1]);
  assert.deepEqual(exportedShared.normalScale, [-0.5, 1.25]);
  assert.equal(exportedShared.normalMapType, "tangent-space");
  assert.equal(exportedShared.emissiveIntensity, 2.5);
  assert.equal(exportedShared.metallic, 0.7);
  assert.equal(exportedShared.roughness, 0.4);
  assert.ok(exportedShared.normalTextureId);
  assert.ok(exportedShared.emissiveTextureId);

  const sharedNode = document.nodes.find((node) => node.name === sharedMesh.name);
  assert.ok(sharedNode);
  const exportedMesh = document.meshes.find((mesh) => mesh.id === sharedNode.meshId);
  assert.ok(exportedMesh);
  assert.equal(exportedMesh.tangents.length, exportedMesh.positions.length / 3 * 4);
  assert.deepEqual(exportedMesh.tangents, tangentValues);
  assert.deepEqual(Array.from((geometry.getAttribute("tangent") as Float32BufferAttribute).array), original.tangent);

  const emissionTrack = document.animations[0].tracks.find((track) =>
    track.targetNodeId === sharedNode.id && track.property === "materialEmissive"
  );
  assert.ok(emissionTrack);
  assert.deepEqual(emissionTrack.times, [0, 1]);
  assert.deepEqual(emissionTrack.values, [0.625, 1.25, 1.875, 1.125, 2.25, 3.375]);

  assert.deepEqual(sharedMaterial.emissive.toArray(), original.emissive);
  assert.equal(sharedMaterial.emissiveIntensity, original.emissiveIntensity);
  assert.equal(sharedMaterial.metalness, original.metalness);
  assert.equal(sharedMaterial.roughness, original.roughness);
  for (const [index, texture] of sourceTextures.entries()) {
    assert.deepEqual(Array.from(texture.image.data), original.textures[index].bytes);
    assert.deepEqual(texture.offset.toArray(), original.textures[index].offset);
    assert.deepEqual(texture.repeat.toArray(), original.textures[index].repeat);
    assert.equal(texture.colorSpace, original.textures[index].colorSpace);
  }

  for (let version = 1; version <= 7; version += 1) {
    const legacy = structuredClone(document) as unknown as Record<string, unknown> & {
      animations: unknown[];
      materials: Array<Record<string, unknown>>;
      meshes: Array<Record<string, unknown>>;
    };
    legacy.version = version;
    for (const mesh of legacy.meshes) delete mesh.tangents;
    for (const material of legacy.materials) {
      for (const field of [
        "metalnessTextureId",
        "roughnessTextureId",
        "metalnessTextureST",
        "roughnessTextureST",
        "normalTextureST",
        "emissiveTextureST",
        "normalMapType",
        "normalScale",
        "emissiveIntensity",
      ]) delete material[field];
    }
    if (version < 4) {
      legacy.animations = [];
      legacy.defaultAnimationId = "";
      legacy.autoplayAnimation = false;
    }
    assert.deepEqual(validateDocument(legacy), { valid: true, errors: [] }, `format v${version} compatibility`);
  }

  const incompatibleDocument = structuredClone(document);
  const malformedSeparate = incompatibleDocument.materials.find((material) => material.name === separateMaterial.name);
  assert.ok(malformedSeparate);
  malformedSeparate.roughnessTextureST[0] += 0.25;
  const incompatibleValidation = validateDocument(incompatibleDocument);
  assert.equal(incompatibleValidation.errors.length, 1);
  assert.match(incompatibleValidation.errors[0], /incompatible texture ST.*same dimensions and sampling transform/);

  const invalidColorDocument = structuredClone(document);
  const invalidColorMaterial = invalidColorDocument.materials.find((material) => material.name === separateMaterial.name);
  assert.ok(invalidColorMaterial);
  const invalidColorTexture = invalidColorDocument.textures.find((texture) => texture.id === invalidColorMaterial.metalnessTextureId);
  assert.ok(invalidColorTexture);
  invalidColorTexture.colorSpace = "srgb";
  const invalidColorValidation = validateDocument(invalidColorDocument);
  assert.equal(invalidColorValidation.errors.length, 1);
  assert.match(invalidColorValidation.errors[0], /metalnessTextureId.*non-color texture.*none or linear/);

  const mismatchedMetalness = makeTexture("Mismatched Metalness", pixels);
  const mismatchedRoughness = makeTexture("Mismatched Roughness", pixels);
  mismatchedRoughness.repeat.set(2, 1);
  const mismatchedMaterial = new MeshStandardMaterial({
    metalnessMap: mismatchedMetalness,
    roughnessMap: mismatchedRoughness,
  });
  mismatchedMaterial.name = "Mismatched PBR Material";
  const mismatchedScene = new Scene();
  mismatchedScene.add(new Mesh(geometry, mismatchedMaterial));
  await assert.rejects(
    exportThreeUnity(mismatchedScene),
    /Mismatched PBR Material.*differ in texture ST.*same dimensions and sampling transform.*one packed texture/,
  );

  const srgbMask = makeTexture("Invalid sRGB Mask", pixels, SRGBColorSpace);
  const srgbMaterial = new MeshStandardMaterial({ metalnessMap: srgbMask });
  srgbMaterial.name = "Invalid Data Color Space";
  const srgbScene = new Scene();
  srgbScene.add(new Mesh(geometry, srgbMaterial));
  await assert.rejects(
    exportThreeUnity(srgbScene),
    /Invalid Data Color Space.*metalnessMap.*Invalid sRGB Mask.*colorSpace 'srgb'.*NoColorSpace or LinearSRGBColorSpace/,
  );

  const mappedAnimatedMaterial = new MeshStandardMaterial({
    metalness: 0.2,
    metalnessMap: sharedMask,
    roughnessMap: sharedMask,
  });
  mappedAnimatedMaterial.name = "Mapped Animated Material";
  const mappedAnimatedMesh = new Mesh(geometry, mappedAnimatedMaterial);
  mappedAnimatedMesh.name = "Mapped Animated Mesh";
  const mappedAnimatedScene = new Scene();
  mappedAnimatedScene.add(mappedAnimatedMesh);
  const mappedClip = new AnimationClip("Mapped Metallic Pulse", 1, [
    new NumberKeyframeTrack(`${mappedAnimatedMesh.uuid}.material.metalness`, [0, 1], [0.2, 0.9]),
  ]);
  await assert.rejects(
    exportThreeUnity(mappedAnimatedScene, { animations: [mappedClip] }),
    /Mapped Metallic Pulse.*Mapped Animated Mesh.*source material index 0.*Mapped Animated Material.*property 'metalness'.*packs static metallic\/roughness.*map-free material/,
  );
  assert.equal(mappedAnimatedMaterial.metalness, 0.2);
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

test("exports Line, LineSegments, LineLoop, Points, and Sprite as format v5 primitives without mutating Three.js state", async () => {
  const scene = new Scene();

  const continuousData = new InterleavedBuffer(new Float32Array([
    0, 0, 0, 10,
    1, 0, 0, 11,
    1, 1, 0, 12,
    0, 1, 0, 13,
  ]), 4);
  const continuousGeometry = new BufferGeometry();
  continuousGeometry.setAttribute("position", new InterleavedBufferAttribute(continuousData, 3, 0));
  continuousGeometry.setIndex([2, 0, 3]);
  const continuous = new Line(continuousGeometry, new LineBasicMaterial({ color: 0xff8844 }));
  continuous.name = "Indexed Continuous Line";

  const segmentsGeometry = new BufferGeometry();
  segmentsGeometry.setAttribute("position", new Float32BufferAttribute([
    -2, 0, 0,
    -1, 1, 0,
    0, 0, 0,
    1, 1, 0,
  ], 3));
  const segments = new LineSegments(segmentsGeometry, new LineBasicMaterial({ color: 0x44aaff }));
  segments.name = "Non Indexed Segments";

  const loopGeometry = new BufferGeometry();
  loopGeometry.setAttribute("position", new Float32BufferAttribute([
    -1, 0, 0,
    0, 1, 0,
    1, 0, 0,
    2, 0, 0,
    3, 1, 0,
    4, 0, 0,
  ], 3));
  loopGeometry.setIndex([0, 1, 2, 3, 4, 5]);
  loopGeometry.addGroup(0, 3, 0);
  loopGeometry.addGroup(3, 3, 1);
  const loop = new LineLoop(loopGeometry, [
    new LineBasicMaterial({ color: 0x33ff66 }),
    new LineBasicMaterial({ color: 0xff33aa }),
  ]);
  loop.name = "Grouped Loops";

  const pointsGeometry = new BufferGeometry();
  pointsGeometry.setAttribute("position", new Float32BufferAttribute([
    -1, -1, 0,
    0, -1, 0,
    1, -1, 0,
    2, -1, 0,
  ], 3));
  pointsGeometry.setAttribute("color", new Float32BufferAttribute([
    1, 0, 0,
    0, 1, 0,
    0, 0, 1,
    1, 1, 0,
  ], 3));
  pointsGeometry.setIndex([3, 1, 2]);
  pointsGeometry.addGroup(0, 1, 0);
  pointsGeometry.addGroup(1, 2, 1);
  const animatedPointsMaterial = new PointsMaterial({ color: 0xffffff, size: 12, sizeAttenuation: false, vertexColors: true });
  const points = new Points(pointsGeometry, [animatedPointsMaterial, new PointsMaterial({ color: 0x66ccff, size: 6 })]);
  points.name = "Colored Indexed Points";

  const spritePixels = new Uint8Array([255, 128, 0, 255]);
  const spriteTexture = new DataTexture(spritePixels, 1, 1, RGBAFormat, UnsignedByteType);
  spriteTexture.name = "Sprite Pixel";
  spriteTexture.repeat.set(0.75, 0.5);
  spriteTexture.offset.set(0.125, 0.25);
  const spriteMaterial = new SpriteMaterial({ map: spriteTexture, color: 0xffffff, rotation: Math.PI / 6, transparent: true });
  spriteMaterial.sizeAttenuation = false;
  const sprite = new Sprite(spriteMaterial);
  sprite.name = "Textured Sprite";
  sprite.center.set(0.25, 0.75);
  sprite.position.set(3, 2, 1);
  sprite.scale.set(-2, 3, 1);

  const meshGeometry = new BufferGeometry();
  meshGeometry.setAttribute("position", new Float32BufferAttribute([
    0, 0, 0,
    1, 0, 0,
    0, 1, 0,
  ], 3));
  meshGeometry.setIndex([0, 1, 2]);
  meshGeometry.morphTargetsRelative = true;
  const lift = new Float32BufferAttribute([
    0, 0, 0.25,
    0, 0, 0.25,
    0, 0, 0.25,
  ], 3);
  lift.name = "Lift";
  meshGeometry.morphAttributes.position = [lift];
  const meshMaterial = new MeshStandardMaterial({ color: 0x335577 });
  const legacyMesh = new Mesh(meshGeometry, meshMaterial);
  legacyMesh.name = "Coexisting Morph Mesh";
  legacyMesh.morphTargetDictionary = { Lift: 0 };
  legacyMesh.morphTargetInfluences = [0.2];

  scene.add(continuous, segments, loop, points, sprite, legacyMesh);
  scene.animations.push(new AnimationClip("Primitive And Mesh Animation", 1, [
    new VectorKeyframeTrack(`${continuous.uuid}.position`, [0, 1], [0, 0, 0, 1, 0, 0]),
    new ColorKeyframeTrack(`${points.uuid}.material[0].color`, [0, 1], [1, 1, 1, 1, 0.25, 0.1]),
    new VectorKeyframeTrack(`${sprite.uuid}.map.offset`, [0, 1], [0.125, 0.25, 0.5, 0.75]),
    new VectorKeyframeTrack(`${legacyMesh.uuid}.morphTargetInfluences`, [0, 1], [0.2, 0.8]),
    new ColorKeyframeTrack(`${legacyMesh.uuid}.material.color`, [0, 1], [...meshMaterial.color.toArray(), 0.8, 0.2, 0.1]),
  ]));

  const original = {
    continuousData: Array.from(continuousData.array),
    continuousIndex: Array.from(continuousGeometry.index!.array),
    continuousPosition: continuous.position.toArray(),
    loopGroups: structuredClone(loopGeometry.groups),
    pointsPositions: Array.from(pointsGeometry.getAttribute("position").array),
    pointsColor: animatedPointsMaterial.color.toArray(),
    spriteCenter: sprite.center.toArray(),
    spriteRotation: spriteMaterial.rotation,
    spriteOffset: spriteTexture.offset.toArray(),
    spriteRepeat: spriteTexture.repeat.toArray(),
    spritePixels: [...spritePixels],
    meshColor: meshMaterial.color.toArray(),
    morphWeights: [...legacyMesh.morphTargetInfluences],
  };

  const document = await exportThreeUnity(scene, { animationSampleRate: 2 });
  assert.deepEqual(validateDocument(document), { valid: true, errors: [] });
  assert.equal(document.version, 8);
  assert.equal(document.primitives.length, 5);

  const primitiveFor = (object: Line | LineSegments | LineLoop | Points | Sprite) => {
    const node = document.nodes.find((candidate) => candidate.name === object.name);
    assert.ok(node);
    assert.equal(node.meshId, "");
    assert.ok(node.primitiveId);
    const primitive = document.primitives.find((candidate) => candidate.id === node.primitiveId);
    assert.ok(primitive);
    return { node, primitive };
  };

  const continuousExport = primitiveFor(continuous);
  assert.equal(continuousExport.primitive.type, "line");
  assert.deepEqual(continuousExport.primitive.positions, [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0]);
  assert.deepEqual(continuousExport.primitive.indices, [2, 0, 0, 3]);
  assert.deepEqual(continuousExport.primitive.groups, [{ start: 0, count: 4, materialIndex: 0 }]);

  const segmentsExport = primitiveFor(segments);
  assert.equal(segmentsExport.primitive.type, "line-segments");
  assert.deepEqual(segmentsExport.primitive.indices, [0, 1, 2, 3]);

  const loopExport = primitiveFor(loop);
  assert.equal(loopExport.primitive.type, "line-loop");
  assert.deepEqual(loopExport.primitive.indices, [0, 1, 1, 2, 2, 0, 3, 4, 4, 5, 5, 3]);
  assert.deepEqual(loopExport.primitive.groups, [
    { start: 0, count: 6, materialIndex: 0 },
    { start: 6, count: 6, materialIndex: 1 },
  ]);

  const pointsExport = primitiveFor(points);
  assert.equal(pointsExport.primitive.type, "points");
  assert.deepEqual(pointsExport.primitive.indices, [3, 1, 2]);
  assert.deepEqual(pointsExport.primitive.groups, [
    { start: 0, count: 1, materialIndex: 0 },
    { start: 1, count: 2, materialIndex: 1 },
  ]);
  assert.deepEqual(pointsExport.primitive.colors, [
    1, 0, 0, 1,
    0, 1, 0, 1,
    0, 0, 1, 1,
    1, 1, 0, 1,
  ]);
  const exportedPointsMaterial = document.materials.find((material) => material.id === pointsExport.primitive.materialIds[0]);
  assert.ok(exportedPointsMaterial);
  assert.equal(exportedPointsMaterial.renderMode, "points");
  assert.equal(exportedPointsMaterial.pointSize, 12);
  assert.equal(exportedPointsMaterial.sizeAttenuation, false);

  const spriteExport = primitiveFor(sprite);
  assert.equal(spriteExport.primitive.type, "sprite");
  assert.deepEqual(spriteExport.primitive.spriteCenter, [0.25, 0.75]);
  assert.deepEqual(spriteExport.primitive.positions, []);
  assert.deepEqual(spriteExport.primitive.indices, []);
  const exportedSpriteMaterial = document.materials.find((material) => material.id === spriteExport.primitive.materialIds[0]);
  assert.ok(exportedSpriteMaterial);
  assert.equal(exportedSpriteMaterial.renderMode, "sprite");
  assert.equal(exportedSpriteMaterial.spriteRotation, Math.PI / 6);
  assert.equal(exportedSpriteMaterial.sizeAttenuation, false);
  const exportedSpriteTexture = document.textures.find((texture) => texture.id === exportedSpriteMaterial.baseColorTextureId);
  assert.ok(exportedSpriteTexture);
  assert.equal(exportedSpriteTexture.data, "/4AA/w==");

  const meshNode = document.nodes.find((node) => node.name === legacyMesh.name);
  assert.ok(meshNode?.meshId);
  assert.equal(meshNode.primitiveId, "");
  assert.equal(document.meshes.find((mesh) => mesh.id === meshNode.meshId)?.morphTargets.length, 1);
  const tracks = document.animations[0].tracks;
  assert.ok(tracks.some((track) => track.targetNodeId === continuousExport.node.id && track.property === "position"));
  assert.ok(tracks.some((track) => track.targetNodeId === pointsExport.node.id && track.property === "materialBaseColor" && track.materialIndex === 0));
  assert.ok(tracks.some((track) => track.targetNodeId === spriteExport.node.id && track.property === "materialBaseMapST" && track.materialIndex === 0));
  assert.ok(tracks.some((track) => track.targetNodeId === meshNode.id && track.property === "morphWeight"));
  assert.ok(tracks.some((track) => track.targetNodeId === meshNode.id && track.property === "materialBaseColor"));

  assert.deepEqual(Array.from(continuousData.array), original.continuousData);
  assert.deepEqual(Array.from(continuousGeometry.index!.array), original.continuousIndex);
  assert.deepEqual(continuous.position.toArray(), original.continuousPosition);
  assert.deepEqual(loopGeometry.groups, original.loopGroups);
  assert.deepEqual(Array.from(pointsGeometry.getAttribute("position").array), original.pointsPositions);
  assert.deepEqual(animatedPointsMaterial.color.toArray(), original.pointsColor);
  assert.deepEqual(sprite.center.toArray(), original.spriteCenter);
  assert.equal(spriteMaterial.rotation, original.spriteRotation);
  assert.deepEqual(spriteTexture.offset.toArray(), original.spriteOffset);
  assert.deepEqual(spriteTexture.repeat.toArray(), original.spriteRepeat);
  assert.deepEqual([...spritePixels], original.spritePixels);
  assert.deepEqual(meshMaterial.color.toArray(), original.meshColor);
  assert.deepEqual(legacyMesh.morphTargetInfluences, original.morphWeights);
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

test("accepts format versions 1 through 6 without later format fields", async () => {
  const scene = new Scene();
  const texture = new DataTexture(new Uint8Array([255, 255, 255, 255]), 1, 1, RGBAFormat, UnsignedByteType);
  const mesh = new Mesh(new BoxGeometry(), new MeshStandardMaterial({ map: texture }));
  scene.add(mesh);
  scene.animations.push(new AnimationClip("Move", 1, [
    new VectorKeyframeTrack(`${mesh.uuid}.position`, [0, 1], [0, 0, 0, 1, 0, 0]),
  ]));
  const currentDocument = await exportThreeUnity(scene);
  const version6Document = structuredClone(currentDocument) as unknown as Record<string, unknown>;
  version6Document.version = 6;
  for (const exportedTexture of version6Document.textures as Array<Record<string, unknown>>) {
    exportedTexture.encoding = "rgba8";
    delete exportedTexture.mimeType;
    delete exportedTexture.pixelFormat;
    delete exportedTexture.componentType;
    delete exportedTexture.filterMode;
    delete exportedTexture.mipmaps;
    delete exportedTexture.anisotropy;
  }
  assert.deepEqual(validateDocument(version6Document), { valid: true, errors: [] });

  const version5Document = structuredClone(version6Document) as unknown as Record<string, unknown>;
  version5Document.version = 5;
  delete version5Document.instancedMeshes;
  for (const node of version5Document.nodes as Array<Record<string, unknown>>) delete node.instancedMeshId;
  assert.deepEqual(validateDocument(version5Document), { valid: true, errors: [] });

  const version4Document = structuredClone(version5Document) as unknown as Record<string, unknown>;
  version4Document.version = 4;
  delete version4Document.primitives;
  for (const node of version4Document.nodes as Array<Record<string, unknown>>) delete node.primitiveId;
  for (const material of version4Document.materials as Array<Record<string, unknown>>) {
    delete material.renderMode;
    delete material.pointSize;
    delete material.sizeAttenuation;
    delete material.spriteRotation;
  }
  assert.deepEqual(validateDocument(version4Document), { valid: true, errors: [] });

  const version3Document = structuredClone(version4Document) as unknown as Record<string, unknown>;
  version3Document.version = 3;
  for (const exportedTexture of version3Document.textures as Array<Record<string, unknown>>) {
    delete exportedTexture.wrapS;
    delete exportedTexture.wrapT;
  }
  for (const material of version3Document.materials as Array<Record<string, unknown>>) delete material.baseColorTextureST;
  for (const animation of version3Document.animations as Array<{ tracks: Array<Record<string, unknown>> }>) {
    for (const track of animation.tracks) delete track.materialIndex;
  }
  assert.deepEqual(validateDocument(version3Document), { valid: true, errors: [] });

  const version2Document = structuredClone(version3Document) as unknown as Record<string, unknown>;
  version2Document.version = 2;
  for (const node of version2Document.nodes as Array<Record<string, unknown>>) delete node.morphWeights;
  for (const mesh of version2Document.meshes as Array<Record<string, unknown>>) delete mesh.morphTargets;
  assert.deepEqual(validateDocument(version2Document), { valid: true, errors: [] });

  const legacyDocument = structuredClone(version2Document) as unknown as Record<string, unknown>;
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
  assert.equal(document.version, 8);
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
  assert.equal(quaternionTrack.morphTargetIndex, -1);
  assert.equal(quaternionTrack.times.at(-1), 1);
  assert.equal(quaternionTrack.values.length, quaternionTrack.times.length * 4);

  const skinnedNode = document.nodes.find((node) => node.skinId === skin.id);
  assert.ok(skinnedNode);
  skinnedNode.meshId = "";
  const missingMeshValidation = validateDocument(document);
  assert.equal(missingMeshValidation.valid, false);
  assert.match(missingMeshValidation.errors.join("\n"), /must reference a mesh/);
});

test("exports absolute and relative morph targets, initial weights, and baked morph animation without mutating the source", async () => {
  const scene = new Scene();
  const material = new MeshStandardMaterial();

  const absoluteGeometry = new BufferGeometry();
  const absolutePositions = new Float32BufferAttribute([
    0, 0, 0,
    1, 0, 0,
    0, 1, 0,
  ], 3);
  const absoluteNormals = new Float32BufferAttribute([
    0, 0, 1,
    0, 0, 1,
    0, 0, 1,
  ], 3);
  absoluteGeometry.setAttribute("position", absolutePositions);
  absoluteGeometry.setAttribute("normal", absoluteNormals);
  const bulgePositions = new Float32BufferAttribute([
    0, 0, 0.5,
    1, 0, 0.25,
    0, 1, 0.5,
  ], 3);
  bulgePositions.name = "Attribute Bulge";
  const twistPositions = new Float32BufferAttribute([
    -0.25, 0, 0,
    1, 0.2, 0,
    0.15, 1, 0,
  ], 3);
  twistPositions.name = "Attribute Twist";
  const bulgeNormals = new Float32BufferAttribute([
    0, 0, 1,
    0, 0, 1,
    0, 0, 1,
  ], 3);
  const twistNormals = new Float32BufferAttribute([
    0.2, 0, 0.8,
    0, 0.2, 0.8,
    -0.2, 0, 0.8,
  ], 3);
  absoluteGeometry.morphAttributes.position = [bulgePositions, twistPositions];
  absoluteGeometry.morphAttributes.normal = [bulgeNormals, twistNormals];
  absoluteGeometry.morphTargetsRelative = false;

  const absoluteMesh = new Mesh(absoluteGeometry, material);
  absoluteMesh.name = "Absolute Morph Mesh";
  absoluteMesh.position.set(2, 3, 4);
  absoluteMesh.morphTargetDictionary = { Bulge: 0, Twist: 1 };
  absoluteMesh.morphTargetInfluences = [0.25, 0];
  scene.add(absoluteMesh);

  const relativeGeometry = new BufferGeometry();
  relativeGeometry.setAttribute("position", new Float32BufferAttribute([
    0, 0, 0,
    1, 0, 0,
    0, 1, 0,
  ], 3));
  relativeGeometry.setAttribute("normal", new Float32BufferAttribute([
    0, 0, 1,
    0, 0, 1,
    0, 0, 1,
  ], 3));
  const relativePositions = new Float32BufferAttribute([
    0, 0, 0.2,
    0, 0, 0.3,
    0, 0, 0.4,
  ], 3);
  relativePositions.name = "Relative Lift";
  const relativeNormals = new Float32BufferAttribute([
    0.1, 0, 0,
    0.1, 0, 0,
    0.1, 0, 0,
  ], 3);
  relativeGeometry.morphAttributes.position = [relativePositions];
  relativeGeometry.morphAttributes.normal = [relativeNormals];
  relativeGeometry.morphTargetsRelative = true;
  const relativeMesh = new Mesh(relativeGeometry, material);
  relativeMesh.name = "Relative Morph Mesh";
  scene.add(relativeMesh);

  const clip = new AnimationClip("Morph Cycle", 1, [
    new VectorKeyframeTrack(`${absoluteMesh.uuid}.morphTargetInfluences`, [0, 0.5, 1], [
      0.25, 0,
      0.75, 1,
      0.25, 0,
    ]),
  ]);
  scene.animations.push(clip);

  const originalInfluenceReference = absoluteMesh.morphTargetInfluences;
  const originalInfluences = [...absoluteMesh.morphTargetInfluences];
  const originalPosition = absoluteMesh.position.toArray();
  const originalQuaternion = absoluteMesh.quaternion.toArray();
  const originalScale = absoluteMesh.scale.toArray();
  const document = await exportThreeUnity(scene, { animationSampleRate: 4 });

  assert.deepEqual(validateDocument(document), { valid: true, errors: [] });
  assert.equal(document.version, 8);
  assert.strictEqual(absoluteMesh.morphTargetInfluences, originalInfluenceReference);
  assert.deepEqual(absoluteMesh.morphTargetInfluences, originalInfluences);
  assert.deepEqual(absoluteMesh.position.toArray(), originalPosition);
  assert.deepEqual(absoluteMesh.quaternion.toArray(), originalQuaternion);
  assert.deepEqual(absoluteMesh.scale.toArray(), originalScale);

  const absoluteNode = document.nodes.find((node) => node.name === absoluteMesh.name);
  assert.ok(absoluteNode);
  assert.deepEqual(absoluteNode.morphWeights, [0.25, 0]);
  const exportedAbsoluteMesh = document.meshes.find((mesh) => mesh.id === absoluteNode.meshId);
  assert.ok(exportedAbsoluteMesh);
  assert.deepEqual(exportedAbsoluteMesh.morphTargets.map((target) => target.name), ["Bulge", "Twist"]);
  assert.ok(Math.abs(exportedAbsoluteMesh.morphTargets[0].positionDeltas[2] - 0.5) < 1e-6);
  assert.ok(Math.abs(exportedAbsoluteMesh.morphTargets[1].positionDeltas[0] - -0.25) < 1e-6);
  assert.ok(Math.abs(exportedAbsoluteMesh.morphTargets[1].normalDeltas[0] - 0.2) < 1e-6);
  assert.ok(Math.abs(exportedAbsoluteMesh.morphTargets[1].normalDeltas[2] - -0.2) < 1e-6);

  const relativeNode = document.nodes.find((node) => node.name === relativeMesh.name);
  assert.ok(relativeNode);
  assert.deepEqual(relativeNode.morphWeights, [0]);
  const exportedRelativeMesh = document.meshes.find((mesh) => mesh.id === relativeNode.meshId);
  assert.ok(exportedRelativeMesh);
  assert.deepEqual(exportedRelativeMesh.morphTargets[0].positionDeltas.slice(0, 3), [0, 0, relativePositions.getZ(0)]);
  assert.ok(Math.abs(exportedRelativeMesh.morphTargets[0].normalDeltas[0] - 0.1) < 1e-6);

  const morphTracks = document.animations[0].tracks.filter((track) => track.property === "morphWeight");
  assert.equal(morphTracks.length, 2);
  assert.deepEqual(morphTracks.map((track) => track.morphTargetIndex), [0, 1]);
  assert.deepEqual(morphTracks[0].times, [0, 0.25, 0.5, 0.75, 1]);
  assert.deepEqual(morphTracks[0].values, [0.25, 0.5, 0.75, 0.5, 0.25]);
  assert.deepEqual(morphTracks[1].values, [0, 0.5, 1, 0.5, 0]);
});

test("exports v4 static UV state and shared material animation without mutating Three.js state", async () => {
  const scene = new Scene();
  const texture = new DataTexture(new Uint8Array([
    255, 32, 32, 255, 32, 255, 64, 255,
    32, 64, 255, 255, 255, 224, 32, 255,
  ]), 2, 2, RGBAFormat, UnsignedByteType);
  texture.name = "Asymmetric Quadrants";
  texture.wrapS = RepeatWrapping;
  texture.wrapT = MirroredRepeatWrapping;
  texture.repeat.set(1.5, 2.5);
  texture.offset.set(0.125, 0.25);

  const sharedMaterial = new MeshStandardMaterial({ map: texture, transparent: true });
  sharedMaterial.name = "Shared Animated Material";
  sharedMaterial.color.setRGB(0.2, 0.4, 0.8);
  sharedMaterial.opacity = 0.8;
  sharedMaterial.emissive.setRGB(0.02, 0.01, 0);
  sharedMaterial.metalness = 0.1;
  sharedMaterial.roughness = 0.6;
  const accentMaterial = new MeshStandardMaterial({ color: 0x444444 });
  accentMaterial.name = "Static Accent";

  const primaryGeometry = new BufferGeometry();
  primaryGeometry.setAttribute("position", new Float32BufferAttribute([
    -0.5, 0, 0,
    0.5, 0, 0,
    0, 1, 0,
  ], 3));
  primaryGeometry.setAttribute("uv", new Float32BufferAttribute([0, 0, 1, 0, 0.5, 1], 2));
  primaryGeometry.setIndex([0, 1, 2]);
  primaryGeometry.computeVertexNormals();
  primaryGeometry.morphAttributes.position = [new Float32BufferAttribute([
    -0.5, 0, 0,
    0.5, 0, 0,
    0, 1, 0.4,
  ], 3)];
  const primary = new Mesh(primaryGeometry, sharedMaterial);
  primary.name = "Primary Shared Mesh";
  primary.morphTargetDictionary = { Lift: 0 };
  primary.morphTargetInfluences = [0.2];

  const groupedGeometry = new BufferGeometry();
  groupedGeometry.setAttribute("position", new Float32BufferAttribute([
    -1, -0.5, 0, 0, -0.5, 0, -0.5, 0.5, 0,
    0, -0.5, 0, 1, -0.5, 0, 0.5, 0.5, 0,
  ], 3));
  groupedGeometry.setAttribute("uv", new Float32BufferAttribute([
    0, 0, 1, 0, 0.5, 1,
    0, 0, 1, 0, 0.5, 1,
  ], 2));
  groupedGeometry.setIndex([0, 1, 2, 3, 4, 5]);
  groupedGeometry.addGroup(0, 3, 0);
  groupedGeometry.addGroup(3, 3, 1);
  groupedGeometry.computeVertexNormals();
  const grouped = new Mesh(groupedGeometry, [accentMaterial, sharedMaterial]);
  grouped.name = "Grouped Shared Mesh";
  grouped.position.x = 2;
  scene.add(primary, grouped);

  const accentColor = accentMaterial.color.toArray();
  const clip = new AnimationClip("Material UV Cycle", 1, [
    new ColorKeyframeTrack(`${primary.uuid}.material.color`, [0, 0.5, 1], [
      0.2, 0.4, 0.8,
      1, 0.15, 0.05,
      0.2, 0.4, 0.8,
    ]),
    new ColorKeyframeTrack(`${grouped.uuid}.material[0].color`, [0, 0.5, 1], [
      ...accentColor,
      0.1, 0.9, 0.3,
      ...accentColor,
    ]),
    new NumberKeyframeTrack(`${primary.uuid}.material.opacity`, [0, 0.5, 1], [0.8, 0.35, 0.8]),
    new ColorKeyframeTrack(`${primary.uuid}.material.emissive`, [0, 0.5, 1], [
      0.02, 0.01, 0,
      0.8, 0.25, 0.05,
      0.02, 0.01, 0,
    ]),
    new NumberKeyframeTrack(`${primary.uuid}.material.metalness`, [0, 0.5, 1], [0.1, 0.85, 0.1]),
    new NumberKeyframeTrack(`${primary.uuid}.material.roughness`, [0, 0.5, 1], [0.6, 0.15, 0.6]),
    new VectorKeyframeTrack(`${primary.uuid}.map.offset`, [0, 0.5, 1], [
      0.125, 0.25,
      0.625, 1.25,
      1.125, 2.25,
    ]),
    new VectorKeyframeTrack(`${primary.uuid}.map.repeat`, [0, 0.5, 1], [
      1.5, 2.5,
      3, 1.25,
      1.5, 2.5,
    ]),
    new VectorKeyframeTrack(`${primary.uuid}.position`, [0, 0.5, 1], [0, 0, 0, 0, 0.25, 0, 0, 0, 0]),
    new VectorKeyframeTrack(`${primary.uuid}.morphTargetInfluences`, [0, 0.5, 1], [0.2, 0.9, 0.2]),
  ]);
  scene.animations.push(clip);

  const original = {
    color: sharedMaterial.color.toArray(),
    opacity: sharedMaterial.opacity,
    emissive: sharedMaterial.emissive.toArray(),
    metalness: sharedMaterial.metalness,
    roughness: sharedMaterial.roughness,
    offset: texture.offset.toArray(),
    repeat: texture.repeat.toArray(),
    accentColor: accentMaterial.color.toArray(),
    position: primary.position.toArray(),
    morphWeights: [...primary.morphTargetInfluences],
  };
  const document = await exportThreeUnity(scene, { animationSampleRate: 2 });

  assert.deepEqual(validateDocument(document), { valid: true, errors: [] });
  assert.equal(document.version, 8);
  assert.equal(document.textures[0].wrapS, "repeat");
  assert.equal(document.textures[0].wrapT, "mirror");
  const exportedSharedMaterial = document.materials.find((material) => material.name === sharedMaterial.name);
  assert.ok(exportedSharedMaterial);
  assert.deepEqual(exportedSharedMaterial.baseColorTextureST, [1.5, 2.5, 0.125, 0.25]);

  const primaryNode = document.nodes.find((node) => node.name === primary.name);
  const groupedNode = document.nodes.find((node) => node.name === grouped.name);
  assert.ok(primaryNode && groupedNode);
  const tracks = document.animations[0].tracks;
  for (const property of ["materialEmissive", "materialMetallic", "materialRoughness", "materialBaseMapST"] as const) {
    const bindings = tracks
      .filter((track) => track.property === property)
      .map((track) => `${track.targetNodeId}:${track.materialIndex}`)
      .sort();
    assert.deepEqual(bindings, [`${primaryNode.id}:0`, `${groupedNode.id}:1`].sort());
  }
  const baseColorBindings = tracks
    .filter((track) => track.property === "materialBaseColor")
    .map((track) => `${track.targetNodeId}:${track.materialIndex}`)
    .sort();
  assert.deepEqual(baseColorBindings, [`${primaryNode.id}:0`, `${groupedNode.id}:0`, `${groupedNode.id}:1`].sort());
  const baseColorTrack = tracks.find((track) => track.property === "materialBaseColor" && track.targetNodeId === primaryNode.id);
  assert.ok(baseColorTrack);
  assert.equal(baseColorTrack.values.length, baseColorTrack.times.length * 4);
  assert.ok(baseColorTrack.values.some((value) => Math.abs(value - 0.35) < 1e-6));
  assert.ok(tracks.some((track) => track.property === "position" && track.materialIndex === -1));
  assert.ok(tracks.some((track) => track.property === "morphWeight" && track.materialIndex === -1));

  assert.deepEqual(sharedMaterial.color.toArray(), original.color);
  assert.equal(sharedMaterial.opacity, original.opacity);
  assert.deepEqual(sharedMaterial.emissive.toArray(), original.emissive);
  assert.equal(sharedMaterial.metalness, original.metalness);
  assert.equal(sharedMaterial.roughness, original.roughness);
  assert.deepEqual(texture.offset.toArray(), original.offset);
  assert.deepEqual(texture.repeat.toArray(), original.repeat);
  assert.deepEqual(accentMaterial.color.toArray(), original.accentColor);
  assert.deepEqual(primary.position.toArray(), original.position);
  assert.deepEqual(primary.morphTargetInfluences, original.morphWeights);
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

test("exports native GPU InstancedMesh records with full matrices and colors while retaining expanded mode", async () => {
  const scene = new Scene();
  const geometry = new BoxGeometry();
  geometry.clearGroups();
  geometry.addGroup(0, 18, 0);
  geometry.addGroup(18, 18, 1);
  const primaryMaterial = new MeshStandardMaterial({ color: 0x80c0ff });
  const accentMaterial = new MeshStandardMaterial({ color: 0xff8050 });
  const instanceCount = 2050;
  const instances = new InstancedMesh(geometry, [primaryMaterial, accentMaterial], instanceCount);
  instances.name = "GPU Test Instances";
  instances.position.set(3, 4, 5);
  const matrix = new Matrix4();
  const color = new Color();
  for (let index = 0; index < instanceCount; index += 1) {
    if (index === instanceCount - 1) {
      matrix.set(
        1, 0.25, 0, 11,
        0, 1, -0.125, 12,
        0, 0, 1, 13,
        0, 0, 0, 1,
      );
    } else {
      matrix.makeTranslation(index % 41, Math.floor(index / 41), -index * 0.01);
    }
    instances.setMatrixAt(index, matrix);
    color.setRGB(index / (instanceCount - 1), (index % 17) / 16, (index % 29) / 28);
    instances.setColorAt(index, color);
  }
  instances.instanceMatrix.needsUpdate = true;
  instances.instanceColor!.needsUpdate = true;
  scene.add(instances);

  const clip = new AnimationClip("Instanced Sweep", 1, [
    new VectorKeyframeTrack(`${instances.uuid}.position`, [0, 1], [3, 4, 5, 4, 4, 5]),
    new ColorKeyframeTrack(`${instances.uuid}.material[1].color`, [0, 1], [1, 0.5, 0.25, 0.25, 0.5, 1]),
  ]);
  scene.animations.push(clip);
  const source = {
    matrices: Array.from(instances.instanceMatrix.array),
    colors: Array.from(instances.instanceColor!.array),
    matrixVersion: instances.instanceMatrix.version,
    colorVersion: instances.instanceColor!.version,
    position: instances.position.toArray(),
    quaternion: instances.quaternion.toArray(),
    scale: instances.scale.toArray(),
    accentColor: accentMaterial.color.toArray(),
  };

  const document = await exportThreeUnity(scene, { animationSampleRate: 2 });
  assert.deepEqual(validateDocument(document), { valid: true, errors: [] });
  assert.equal(document.version, 8);
  assert.equal(document.meshes.length, 1);
  assert.equal(document.instancedMeshes.length, 1);
  assert.equal(document.nodes.length, 2);

  const node = document.nodes.find((candidate) => candidate.name === instances.name);
  assert.ok(node);
  const record = document.instancedMeshes[0];
  assert.equal(node.instancedMeshId, record.id);
  assert.equal(node.meshId, "");
  assert.equal(node.primitiveId, "");
  assert.equal(record.name, "GPU Test Instances Instances");
  assert.equal(record.count, instanceCount);
  assert.equal(record.matrices.length, instanceCount * 16);
  assert.equal(record.colors.length, instanceCount * 4);
  for (const index of [0, 1024, instanceCount - 1]) {
    assert.deepEqual(record.matrices.slice(index * 16, index * 16 + 16), source.matrices.slice(index * 16, index * 16 + 16));
    assert.deepEqual(record.colors.slice(index * 4, index * 4 + 4), [...source.colors.slice(index * 3, index * 3 + 3), 1]);
  }
  assert.equal(record.matrices[(instanceCount - 1) * 16 + 4], 0.25);
  assert.equal(record.matrices[(instanceCount - 1) * 16 + 9], -0.125);
  assert.equal(record.meshId, document.meshes[0].id);
  assert.deepEqual(document.meshes[0].groups, [
    { start: 0, count: 18, materialIndex: 0 },
    { start: 18, count: 18, materialIndex: 1 },
  ]);
  assert.deepEqual(document.meshes[0].materialIds, document.materials.map((material) => material.id));
  assert.ok(document.animations[0].tracks.some((track) => track.targetNodeId === node.id && track.property === "position"));
  assert.ok(!document.animations[0].tracks.some((track) => track.property === "materialBaseColor"));
  assert.match(
    document.warnings.join("\n"),
    /Animation 'Instanced Sweep'.*node 'GPU Test Instances'.*material index 1.*property 'material\.color'/,
  );

  assert.deepEqual(Array.from(instances.instanceMatrix.array), source.matrices);
  assert.deepEqual(Array.from(instances.instanceColor!.array), source.colors);
  assert.equal(instances.instanceMatrix.version, source.matrixVersion);
  assert.equal(instances.instanceColor!.version, source.colorVersion);
  assert.deepEqual(instances.position.toArray(), source.position);
  assert.deepEqual(instances.quaternion.toArray(), source.quaternion);
  assert.deepEqual(instances.scale.toArray(), source.scale);
  assert.deepEqual(accentMaterial.color.toArray(), source.accentColor);

  const nonAffineDocument = structuredClone(document);
  nonAffineDocument.instancedMeshes[0].matrices[3] = 0.5;
  assert.match(validateDocument(nonAffineDocument).errors.join("\n"), /must be an affine matrix/);
  const conflictingNodeDocument = structuredClone(document);
  const conflictingNode = conflictingNodeDocument.nodes.find((candidate) => candidate.instancedMeshId);
  assert.ok(conflictingNode);
  conflictingNode.meshId = record.meshId;
  assert.match(validateDocument(conflictingNodeDocument).errors.join("\n"), /only one of meshId, primitiveId, or instancedMeshId/);

  const expanded = await exportThreeUnity(scene, { instancedMeshMode: "expanded", animationSampleRate: 2 });
  assert.deepEqual(validateDocument(expanded), { valid: true, errors: [] });
  assert.equal(expanded.instancedMeshes.length, 0);
  assert.equal(expanded.nodes.length, instanceCount + 2);
  const expandedParent = expanded.nodes.find((candidate) => candidate.name === instances.name);
  assert.ok(expandedParent);
  assert.equal(expandedParent.instancedMeshId, "");
  const expandedChildren = expanded.nodes.filter((candidate) => candidate.parentId === expandedParent.id && candidate.meshId);
  assert.equal(expandedChildren.length, instanceCount);
  const expandedMaterialTracks = expanded.animations[0].tracks.filter((track) => track.property === "materialBaseColor");
  assert.equal(expandedMaterialTracks.length, instanceCount);
  const expandedChildIds = new Set(expandedChildren.map((child) => child.id));
  assert.ok(expandedMaterialTracks.every((track) => expandedChildIds.has(track.targetNodeId)));
  assert.match(expanded.warnings.join("\n"), /per-instance colors are not exported when instancedMeshMode is 'expanded'/);
  assert.deepEqual(accentMaterial.color.toArray(), source.accentColor);

  const emptyScene = new Scene();
  const emptyInstances = new InstancedMesh(new BoxGeometry(), new MeshStandardMaterial(), 1);
  emptyInstances.count = 0;
  emptyScene.add(emptyInstances);
  const emptyDocument = await exportThreeUnity(emptyScene);
  assert.deepEqual(validateDocument(emptyDocument), { valid: true, errors: [] });
  assert.equal(emptyDocument.instancedMeshes[0].count, 0);
  assert.deepEqual(emptyDocument.instancedMeshes[0].matrices, []);
  assert.deepEqual(emptyDocument.instancedMeshes[0].colors, []);
});

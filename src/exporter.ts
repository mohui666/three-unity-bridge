import {
  AnimationClip,
  AnimationMixer,
  BufferAttribute,
  BufferGeometry,
  Camera,
  Color,
  FrontSide,
  LoopOnce,
  Material,
  Mesh,
  MeshStandardMaterial,
  Object3D,
  PropertyBinding,
  Quaternion,
  Scene,
  SkinnedMesh,
  Texture,
  Vector3,
} from "three";
import {
  THREE_UNITY_FORMAT,
  THREE_UNITY_VERSION,
  ThreeUnityAnimation,
  ThreeUnityAnimationProperty,
  ThreeUnityAnimationTrack,
  ThreeUnityCamera,
  ThreeUnityComponent,
  ThreeUnityDocument,
  ThreeUnityLight,
  ThreeUnityMaterial,
  ThreeUnityMesh,
  ThreeUnityNode,
  ThreeUnityRuntime,
  ThreeUnitySkin,
  ThreeUnityTexture,
} from "./schema.js";

export interface ThreeUnityExportOptions {
  name?: string;
  unitScaleMeters?: number;
  pretty?: boolean;
  includeInvisible?: boolean;
  /** Objects rendered with the scene but not parented under it (commonly cameras). */
  extraObjects?: Object3D[];
  /** Data-driven Unity scene setup. Game-specific code stays in this adapter data. */
  runtime?: Partial<ThreeUnityRuntime>;
  /** Explicit clips override root.animations, including when this is an empty array. */
  animations?: AnimationClip[];
  /** Clip name, uuid, or clip object to select as the default. The first exported clip is used when omitted. */
  defaultAnimation?: string | AnimationClip;
  autoplayAnimation?: boolean;
  animationLoop?: boolean;
  /** Fixed sampling rate used to bake Three.js animation into local transform tracks. */
  animationSampleRate?: number;
}

export interface ThreeUnityUserData {
  unity?: {
    components?: Array<{ type: string; data?: unknown }>;
  };
  [key: string]: unknown;
}

export async function exportThreeUnity(
  root: Scene | Object3D,
  options: ThreeUnityExportOptions = {},
): Promise<ThreeUnityDocument> {
  const nodes: ThreeUnityNode[] = [];
  const meshes: ThreeUnityMesh[] = [];
  const skins: ThreeUnitySkin[] = [];
  const materials: ThreeUnityMaterial[] = [];
  const textures: ThreeUnityTexture[] = [];
  const warnings: string[] = [];
  const warnedRenderableTypes = new Set<string>();
  const meshIds = new Map<string, string>();
  const skinIds = new Map<SkinnedMesh, string>();
  const materialIds = new Map<Material, string>();
  const textureIds = new Map<Texture, string>();
  const visitedObjects = new Set<Object3D>();
  const requiredBones = new Set<Object3D>();
  const nodeIdFor = (object: Object3D): string => stableId("node", object.uuid, 0);

  const registerTexture = async (texture: Texture | null | undefined): Promise<string> => {
    if (!texture) return "";
    const existing = textureIds.get(texture);
    if (existing) return existing;
    const id = stableId("texture", texture.uuid, textureIds.size);
    textureIds.set(texture, id);
    const encoded = await encodeTexture(texture, warnings);
    textures.push({ id, name: texture.name || id, ...encoded, flipY: texture.flipY, colorSpace: texture.colorSpace });
    return id;
  };

  const registerMaterial = async (material: Material): Promise<string> => {
    const existing = materialIds.get(material);
    if (existing) return existing;
    const id = stableId("material", material.uuid, materialIds.size);
    materialIds.set(material, id);
    const standard = material as MeshStandardMaterial;
    if (!new Set(["MeshBasicMaterial", "MeshStandardMaterial", "MeshPhysicalMaterial", "MeshLambertMaterial", "MeshPhongMaterial"]).has(material.type)) {
      warnings.push(`${material.name || material.uuid}: ${material.type} is approximated with a Unity Lit material.`);
    }
    const baseColor = readColor((material as Material & { color?: Color }).color, [1, 1, 1]);
    const emissive = readColor(standard.emissive, [0, 0, 0]);
    const converted: ThreeUnityMaterial = {
      id,
      name: material.name || id,
      sourceType: material.type,
      baseColor: [baseColor[0], baseColor[1], baseColor[2], material.opacity],
      emissive,
      metallic: typeof standard.metalness === "number" ? standard.metalness : 0,
      roughness: typeof standard.roughness === "number" ? standard.roughness : 0.5,
      opacity: material.opacity,
      transparent: material.transparent,
      // Unity v1 has no back-side-only mode; two-sided is the closest safe
      // representation for Three.js BackSide materials such as sky domes.
      doubleSided: material.side !== FrontSide,
      alphaCutoff: material.alphaTest,
      unlit: material.type === "MeshBasicMaterial" || Boolean((material as Material & { isMeshBasicMaterial?: boolean }).isMeshBasicMaterial),
      vertexColors: Boolean((material as Material & { vertexColors?: boolean }).vertexColors),
      baseColorTextureId: await registerTexture(standard.map),
      emissiveTextureId: await registerTexture(standard.emissiveMap),
      normalTextureId: await registerTexture(standard.normalMap),
      metallicRoughnessTextureId: await registerTexture(standard.metalnessMap || standard.roughnessMap),
    };
    materials.push(converted);
    return id;
  };

  const registerMesh = async (mesh: Mesh): Promise<string> => {
    const geometry = mesh.geometry;
    const meshMaterials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    const skinnedMesh = mesh as SkinnedMesh;
    const isSkinnedMesh = Boolean(skinnedMesh.isSkinnedMesh);
    // Unity's imported subasset owns its material slots, so meshes that share
    // geometry but override materials must remain separate bridge records.
    const meshKey = `${geometry.uuid}|${meshMaterials.map((material) => material.uuid).join("|")}|${isSkinnedMesh ? "skinned" : "static"}`;
    const existing = meshIds.get(meshKey);
    if (existing) return existing;
    const id = stableId("mesh", meshKey.replaceAll("|", "_"), meshIds.size);
    meshIds.set(meshKey, id);
    const materialIdList = await Promise.all(meshMaterials.map(registerMaterial));
    const position = requiredAttribute(geometry, "position");
    const normal = geometry.getAttribute("normal") as BufferAttribute | undefined;
    const uv = geometry.getAttribute("uv") as BufferAttribute | undefined;
    const color = geometry.getAttribute("color") as BufferAttribute | undefined;
    if (geometry.morphAttributes.position?.length) warnings.push(`${mesh.name || mesh.uuid}: morph targets are not exported in format v2.`);
    const skinAttributes = isSkinnedMesh ? normalizeSkinAttributes(skinnedMesh, position) : { skinIndices: [], skinWeights: [] };
    meshes.push({
      id,
      name: geometry.name || mesh.name || id,
      positions: attributeToArray(position),
      normals: normal ? attributeToArray(normal) : [],
      uv0: uv ? attributeToArray(uv) : [],
      colors: color ? attributeToArray(color, 4, 1) : [],
      indices: geometry.index ? Array.from(geometry.index.array, Number) : [],
      groups: geometry.groups.map((group) => ({ start: group.start, count: group.count, materialIndex: group.materialIndex ?? 0 })),
      materialIds: materialIdList,
      ...skinAttributes,
    });
    return id;
  };

  const registerSkin = (mesh: SkinnedMesh, meshNodeId: string): string => {
    const existing = skinIds.get(mesh);
    if (existing) return existing;
    if (!mesh.skeleton || mesh.skeleton.bones.length === 0) throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' has no skeleton bones.`);
    if (mesh.skeleton.boneInverses.length !== mesh.skeleton.bones.length) {
      throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' has ${mesh.skeleton.bones.length} bones but ${mesh.skeleton.boneInverses.length} inverse bind matrices.`);
    }
    for (const bone of mesh.skeleton.bones) requiredBones.add(bone);
    const boneSet = new Set<Object3D>(mesh.skeleton.bones);
    const rootBone = mesh.skeleton.bones.find((bone) => !bone.parent || !boneSet.has(bone.parent)) ?? mesh.skeleton.bones[0];
    const id = stableId("skin", mesh.uuid, skinIds.size);
    skinIds.set(mesh, id);
    skins.push({
      id,
      name: `${mesh.name || "SkinnedMesh"} Skin`,
      meshNodeId,
      boneNodeIds: mesh.skeleton.bones.map(nodeIdFor),
      rootBoneNodeId: nodeIdFor(rootBone),
      inverseBindMatrices: mesh.skeleton.boneInverses.flatMap((matrix) => finiteMatrixElements(matrix.elements, `${mesh.name || mesh.uuid} inverse bind matrix`)),
      bindMatrix: finiteMatrixElements(mesh.bindMatrix.elements, `${mesh.name || mesh.uuid} bind matrix`),
    });
    return id;
  };

  const visit = async (object: Object3D, parentId: string, force = false): Promise<void> => {
    if (visitedObjects.has(object)) return;
    if (!force && !options.includeInvisible && !object.visible) return;
    visitedObjects.add(object);
    const id = nodeIdFor(object);
    const userData = object.userData as ThreeUnityUserData;
    const components: ThreeUnityComponent[] = (userData.unity?.components ?? []).map((component) => ({
      type: component.type,
      dataJson: safeJson(component.data ?? {}),
    }));
    const metadata = { ...userData };
    delete metadata.unity;
    const node: ThreeUnityNode = {
      id,
      name: object.name || object.type,
      parentId,
      visible: object.visible,
      position: [object.position.x, object.position.y, object.position.z],
      quaternion: [object.quaternion.x, object.quaternion.y, object.quaternion.z, object.quaternion.w],
      scale: [object.scale.x, object.scale.y, object.scale.z],
      layersMask: object.layers.mask,
      meshId: "",
      skinId: "",
      metadataJson: safeJson(metadata),
      components,
    };
    const isInstancedMesh = Boolean((object as Mesh & { isInstancedMesh?: boolean }).isInstancedMesh);
    if ((object as Mesh).isMesh && !isInstancedMesh) {
      node.meshId = await registerMesh(object as Mesh);
      if ((object as SkinnedMesh).isSkinnedMesh) node.skinId = registerSkin(object as SkinnedMesh, id);
    }
    if (!(object as Mesh).isMesh && isUnsupportedRenderable(object)) {
      if (!warnedRenderableTypes.has(object.type)) {
        warnings.push(`${object.type} renderables are preserved as transforms only in format v2.`);
        warnedRenderableTypes.add(object.type);
      }
    }
    if ((object as Camera).isCamera) node.camera = exportCamera(object as Camera);
    if ((object as Object3D & { isLight?: boolean }).isLight) node.light = exportLight(object as Object3D & LightLike);
    nodes.push(node);
    if (isInstancedMesh) await exportInstances(object as Mesh & InstancedMeshLike, id, registerMesh, nodes, warnings);
    for (const child of object.children) await visit(child, id, requiredBones.has(child));
  };

  // Export the supplied root as a real node so its transform and metadata survive.
  await visit(root, "");
  for (const object of options.extraObjects ?? []) await visit(object, "");
  for (const bone of requiredBones) {
    if (visitedObjects.has(bone)) continue;
    let detachedRoot = bone;
    while (detachedRoot.parent && requiredBones.has(detachedRoot.parent) && !visitedObjects.has(detachedRoot.parent)) detachedRoot = detachedRoot.parent;
    const parentId = detachedRoot.parent && visitedObjects.has(detachedRoot.parent) ? nodeIdFor(detachedRoot.parent) : "";
    await visit(detachedRoot, parentId, true);
  }
  const animationResult = exportAnimations(root, options.animations ?? root.animations, options, visitedObjects, nodeIdFor, warnings);
  return {
    format: THREE_UNITY_FORMAT,
    version: THREE_UNITY_VERSION,
    generator: "three-unity-bridge/0.1.0",
    name: options.name || root.name || "Three.js Scene",
    coordinateSystem: "threejs-right-handed-y-up",
    unitScaleMeters: options.unitScaleMeters ?? 1,
    nodes,
    meshes,
    skins,
    animations: animationResult.animations,
    defaultAnimationId: animationResult.defaultAnimationId,
    autoplayAnimation: animationResult.autoplayAnimation,
    materials,
    textures,
    runtime: normalizeRuntime(options.runtime),
    warnings: [...new Set(warnings)],
  };
}

interface SavedTransform {
  position: [number, number, number];
  quaternion: [number, number, number, number];
  scale: [number, number, number];
}

interface SampledTransform {
  position: number[];
  quaternion: number[];
  scale: number[];
}

function exportAnimations(
  root: Object3D,
  clips: AnimationClip[],
  options: ThreeUnityExportOptions,
  visitedObjects: Set<Object3D>,
  nodeIdFor: (object: Object3D) => string,
  warnings: string[],
): { animations: ThreeUnityAnimation[]; defaultAnimationId: string; autoplayAnimation: boolean } {
  const sampleRate = options.animationSampleRate ?? 30;
  if (!Number.isFinite(sampleRate) || sampleRate <= 0) throw new Error(`animationSampleRate must be a positive finite number, received '${sampleRate}'.`);
  const exportedObjects = [...visitedObjects];
  const exportedObjectSet = new Set(exportedObjects);
  const animations: ThreeUnityAnimation[] = [];
  const sourceByAnimationId = new Map<string, AnimationClip>();

  for (const [clipIndex, clip] of clips.entries()) {
    if (!Number.isFinite(clip.duration) || clip.duration <= 0) {
      warnings.push(`${clip.name || clip.uuid}: animation duration must be positive; the clip was not exported.`);
      continue;
    }
    if (clip.tracks.length === 0) warnings.push(`${clip.name || clip.uuid}: empty animation clip has no tracks.`);
    const properties = new Set<ThreeUnityAnimationProperty>();
    const supportedTracks = clip.tracks.filter((track) => {
      let parsed: ReturnType<typeof PropertyBinding.parseTrackName>;
      try {
        parsed = PropertyBinding.parseTrackName(track.name);
      } catch (error) {
        warnings.push(`${clip.name || clip.uuid}: track '${track.name}' could not be parsed (${error instanceof Error ? error.message : String(error)}).`);
        return false;
      }
      if (!isAnimationProperty(parsed.propertyName)) {
        warnings.push(`${clip.name || clip.uuid}: track '${track.name}' targets unsupported property '${parsed.propertyName}'.`);
        return false;
      }
      if (parsed.nodeName) {
        const target = PropertyBinding.findNode(root, parsed.nodeName);
        if (!target) {
          warnings.push(`${clip.name || clip.uuid}: track '${track.name}' does not resolve to a node under the exported root.`);
          return false;
        }
        if (parsed.objectName === undefined && !exportedObjectSet.has(target as Object3D)) {
          warnings.push(`${clip.name || clip.uuid}: track '${track.name}' resolves to a node that was not exported.`);
          return false;
        }
      }
      properties.add(parsed.propertyName);
      return true;
    });
    const id = stableId("animation", clip.uuid, clipIndex);
    const tracks = bakeAnimationClip(root, clip, supportedTracks, properties, sampleRate, exportedObjects, nodeIdFor);
    if (supportedTracks.length > 0 && tracks.length === 0) {
      warnings.push(`${clip.name || clip.uuid}: supported tracks produced no exported transform changes.`);
    }
    animations.push({
      id,
      name: clip.name || id,
      duration: clip.duration,
      loop: options.animationLoop ?? true,
      tracks,
    });
    sourceByAnimationId.set(id, clip);
  }

  let defaultAnimation: ThreeUnityAnimation | undefined = animations[0];
  if (options.defaultAnimation !== undefined) {
    defaultAnimation = animations.find((animation) => {
      const source = sourceByAnimationId.get(animation.id);
      return typeof options.defaultAnimation === "string"
        ? animation.name === options.defaultAnimation || animation.id === options.defaultAnimation || source?.uuid === options.defaultAnimation
        : source === options.defaultAnimation;
    });
    if (!defaultAnimation) {
      const requested = typeof options.defaultAnimation === "string" ? options.defaultAnimation : options.defaultAnimation.name || options.defaultAnimation.uuid;
      throw new Error(`Default animation '${requested}' was not exported.`);
    }
  }
  const defaultAnimationId = defaultAnimation?.id ?? "";
  const autoplayAnimation = options.autoplayAnimation ?? Boolean(defaultAnimationId);
  if (autoplayAnimation && !defaultAnimationId) throw new Error("autoplayAnimation requires at least one exported animation.");
  return { animations, defaultAnimationId, autoplayAnimation };
}

function bakeAnimationClip(
  root: Object3D,
  clip: AnimationClip,
  sourceTracks: AnimationClip["tracks"],
  properties: Set<ThreeUnityAnimationProperty>,
  sampleRate: number,
  objects: Object3D[],
  nodeIdFor: (object: Object3D) => string,
): ThreeUnityAnimationTrack[] {
  if (sourceTracks.length === 0) return [];
  const saved = new Map<Object3D, SavedTransform>();
  const sampled = new Map<Object3D, SampledTransform>();
  for (const object of objects) {
    saved.set(object, {
      position: [object.position.x, object.position.y, object.position.z],
      quaternion: [object.quaternion.x, object.quaternion.y, object.quaternion.z, object.quaternion.w],
      scale: [object.scale.x, object.scale.y, object.scale.z],
    });
    sampled.set(object, { position: [], quaternion: [], scale: [] });
  }

  const times = createAnimationSampleTimes(clip.duration, sampleRate);
  const sampledClip = new AnimationClip(`${clip.name || clip.uuid} (bridge bake)`, clip.duration, sourceTracks);
  const mixer = new AnimationMixer(root);
  const action = mixer.clipAction(sampledClip);
  action.setLoop(LoopOnce, 1);
  action.clampWhenFinished = true;
  action.play();
  try {
    for (const time of times) {
      mixer.setTime(time);
      for (const object of objects) {
        const values = sampled.get(object)!;
        if (properties.has("position")) values.position.push(object.position.x, object.position.y, object.position.z);
        if (properties.has("quaternion")) appendContinuousQuaternion(values.quaternion, object.quaternion, saved.get(object)!.quaternion);
        if (properties.has("scale")) values.scale.push(object.scale.x, object.scale.y, object.scale.z);
      }
    }
  } finally {
    mixer.stopAllAction();
    mixer.uncacheClip(sampledClip);
    mixer.uncacheRoot(root);
    for (const [object, transform] of saved) {
      object.position.fromArray(transform.position);
      object.quaternion.fromArray(transform.quaternion);
      object.scale.fromArray(transform.scale);
    }
    root.updateMatrixWorld(true);
  }

  const tracks: ThreeUnityAnimationTrack[] = [];
  for (const object of objects) {
    const original = saved.get(object)!;
    const values = sampled.get(object)!;
    for (const property of properties) {
      const dimensions = property === "quaternion" ? 4 : 3;
      if (!sampledValuesChange(values[property], original[property], dimensions)) continue;
      tracks.push({
        targetNodeId: nodeIdFor(object),
        property,
        times: [...times],
        values: values[property],
        interpolation: "linear",
        baked: true,
      });
    }
  }
  return tracks;
}

function createAnimationSampleTimes(duration: number, sampleRate: number): number[] {
  const frameCount = Math.ceil(duration * sampleRate);
  const times: number[] = [];
  for (let frame = 0; frame < frameCount; frame += 1) times.push(frame / sampleRate);
  times.push(duration);
  return times;
}

function appendContinuousQuaternion(values: number[], quaternion: Quaternion, original: SavedTransform["quaternion"]): void {
  let x = quaternion.x;
  let y = quaternion.y;
  let z = quaternion.z;
  let w = quaternion.w;
  const offset = values.length - 4;
  const reference = offset >= 0 ? values.slice(offset, offset + 4) : original;
  if (reference[0] * x + reference[1] * y + reference[2] * z + reference[3] * w < 0) {
    x = -x;
    y = -y;
    z = -z;
    w = -w;
  }
  values.push(x, y, z, w);
}

function sampledValuesChange(values: number[], original: readonly number[], dimensions: number): boolean {
  for (let offset = 0; offset < values.length; offset += dimensions) {
    for (let component = 0; component < dimensions; component += 1) {
      if (Math.abs(values[offset + component] - original[component]) > 1e-7) return true;
    }
  }
  return false;
}

function isAnimationProperty(value: string): value is ThreeUnityAnimationProperty {
  return value === "position" || value === "quaternion" || value === "scale";
}

function normalizeRuntime(runtime: Partial<ThreeUnityRuntime> | undefined): ThreeUnityRuntime {
  return {
    controller: runtime?.controller ?? "none",
    colliderMode: runtime?.colliderMode ?? "none",
    enableBlockEditing: runtime?.enableBlockEditing ?? false,
    allowFly: runtime?.allowFly ?? false,
    hudStyle: runtime?.hudStyle ?? "diagnostic",
    moveSpeed: runtime?.moveSpeed ?? 5.5,
    sprintSpeed: runtime?.sprintSpeed ?? 9,
    flySpeed: runtime?.flySpeed ?? 8,
    hotbar: (runtime?.hotbar ?? []).map((item) => ({ name: item.name, color: [...item.color] as [number, number, number, number] })),
  };
}

function isUnsupportedRenderable(object: Object3D): boolean {
  const value = object as Object3D & { isLine?: boolean; isPoints?: boolean; isSprite?: boolean };
  return Boolean(value.isLine || value.isPoints || value.isSprite);
}

export async function exportThreeUnityJson(root: Scene | Object3D, options: ThreeUnityExportOptions = {}): Promise<string> {
  return JSON.stringify(await exportThreeUnity(root, options), null, options.pretty === false ? undefined : 2);
}

export function downloadThreeUnity(json: string, fileName = "scene.threeunity"): void {
  if (typeof document === "undefined") throw new Error("downloadThreeUnity is only available in a browser.");
  const blob = new Blob([json], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

function exportCamera(camera: Camera): ThreeUnityCamera {
  const value = camera as Camera & CameraLike;
  if (value.isPerspectiveCamera) {
    return { type: "perspective", fov: value.fov ?? 50, near: value.near ?? 0.1, far: value.far ?? 2000, aspect: value.aspect ?? 1, left: 0, right: 0, top: 0, bottom: 0 };
  }
  if (value.isOrthographicCamera) {
    return { type: "orthographic", fov: 0, near: value.near ?? 0.1, far: value.far ?? 2000, aspect: 1, left: value.left ?? -1, right: value.right ?? 1, top: value.top ?? 1, bottom: value.bottom ?? -1 };
  }
  throw new Error(`Unsupported camera type '${camera.type}'.`);
}

interface LightLike extends Object3D {
  color: Color;
  intensity: number;
  distance?: number;
  angle?: number;
  penumbra?: number;
  castShadow: boolean;
  isDirectionalLight?: boolean;
  isSpotLight?: boolean;
  isAmbientLight?: boolean;
  isHemisphereLight?: boolean;
  isPointLight?: boolean;
}

interface CameraLike {
  isPerspectiveCamera?: boolean;
  isOrthographicCamera?: boolean;
  fov?: number;
  near?: number;
  far?: number;
  aspect?: number;
  left?: number;
  right?: number;
  top?: number;
  bottom?: number;
}

interface InstancedMeshLike {
  count: number;
  instanceColor?: unknown;
  getMatrixAt(index: number, matrix: import("three").Matrix4): void;
}

function exportLight(light: LightLike): ThreeUnityLight {
  let type: ThreeUnityLight["type"] = "point";
  if (light.isDirectionalLight) type = "directional";
  else if (light.isSpotLight) type = "spot";
  else if (light.isAmbientLight || light.isHemisphereLight) type = "ambient";
  const distance = typeof light.distance === "number" ? light.distance : 0;
  const angle = light.isSpotLight ? light.angle ?? Math.PI / 4 : Math.PI / 4;
  return {
    type,
    color: [light.color.r, light.color.g, light.color.b],
    intensity: light.intensity,
    range: distance,
    spotAngleRadians: angle * 2,
    penumbra: light.isSpotLight ? light.penumbra ?? 0 : 0,
    castShadow: light.castShadow,
  };
}

async function exportInstances(
  object: Mesh & InstancedMeshLike,
  parentId: string,
  registerMesh: (mesh: Mesh) => Promise<string>,
  nodes: ThreeUnityNode[],
  warnings: string[],
): Promise<void> {
  const meshId = await registerMesh(object);
  const matrix = object.matrix.clone();
  const position = new Vector3();
  const rotation = new Quaternion();
  const scale = new Vector3();
  for (let index = 0; index < object.count; index += 1) {
    object.getMatrixAt(index, matrix);
    matrix.decompose(position, rotation, scale);
    nodes.push({
      id: `${stableId("node", object.uuid, nodes.length)}_instance_${index}`,
      name: `${object.name || "InstancedMesh"} ${index}`,
      parentId,
      visible: true,
      position: [position.x, position.y, position.z],
      quaternion: [rotation.x, rotation.y, rotation.z, rotation.w],
      scale: [scale.x, scale.y, scale.z],
      layersMask: object.layers.mask,
      meshId,
      skinId: "",
      metadataJson: `{\"threeUnityInstance\":${index}}`,
      components: [],
    });
  }
  if (object.instanceColor) warnings.push(`${object.name || object.uuid}: per-instance colors are not exported in format v2.`);
}

function normalizeSkinAttributes(mesh: SkinnedMesh, position: BufferAttribute): Pick<ThreeUnityMesh, "skinIndices" | "skinWeights"> {
  if (!mesh.skeleton || mesh.skeleton.bones.length === 0) throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' has no skeleton bones.`);
  const skinIndex = mesh.geometry.getAttribute("skinIndex") as BufferAttribute | undefined;
  const skinWeight = mesh.geometry.getAttribute("skinWeight") as BufferAttribute | undefined;
  if (!skinIndex || !skinWeight) throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' requires both 'skinIndex' and 'skinWeight' attributes.`);
  if (skinIndex.count !== position.count || skinWeight.count !== position.count || skinIndex.itemSize !== skinWeight.itemSize) {
    throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' skin attributes must have matching vertex counts and item sizes.`);
  }

  const skinIndices: number[] = [];
  const skinWeights: number[] = [];
  for (let vertex = 0; vertex < position.count; vertex += 1) {
    const influences: Array<{ boneIndex: number; weight: number; slot: number }> = [];
    for (let slot = 0; slot < skinIndex.itemSize; slot += 1) {
      const boneIndex = skinIndex.getComponent(vertex, slot);
      const weight = skinWeight.getComponent(vertex, slot);
      if (!Number.isInteger(boneIndex) || boneIndex < 0 || boneIndex >= mesh.skeleton.bones.length) {
        throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' vertex ${vertex} has invalid bone index '${boneIndex}'.`);
      }
      if (!Number.isFinite(weight) || weight < 0) throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' vertex ${vertex} has invalid skin weight '${weight}'.`);
      if (weight > 0) influences.push({ boneIndex, weight, slot });
    }
    influences.sort((left, right) => right.weight - left.weight || left.slot - right.slot);
    const retained = influences.slice(0, 4);
    const weightSum = retained.reduce((sum, influence) => sum + influence.weight, 0);
    if (weightSum <= 0) throw new Error(`SkinnedMesh '${mesh.name || mesh.uuid}' vertex ${vertex} has no positive skin weight.`);
    for (let slot = 0; slot < 4; slot += 1) {
      const influence = retained[slot];
      skinIndices.push(influence?.boneIndex ?? 0);
      skinWeights.push(influence ? influence.weight / weightSum : 0);
    }
  }
  return { skinIndices, skinWeights };
}

function finiteMatrixElements(elements: readonly number[], label: string): number[] {
  if (elements.length !== 16 || elements.some((value) => !Number.isFinite(value))) throw new Error(`${label} must contain 16 finite values.`);
  return Array.from(elements);
}

function requiredAttribute(geometry: BufferGeometry, name: string): BufferAttribute {
  const value = geometry.getAttribute(name);
  if (!value) throw new Error(`Geometry '${geometry.name || geometry.uuid}' has no '${name}' attribute.`);
  return value as BufferAttribute;
}

function attributeToArray(attribute: BufferAttribute, targetItemSize = attribute.itemSize, fill = 0): number[] {
  const output: number[] = [];
  for (let index = 0; index < attribute.count; index += 1) {
    for (let component = 0; component < targetItemSize; component += 1) {
      output.push(component < attribute.itemSize ? attribute.getComponent(index, component) : fill);
    }
  }
  return output;
}

function readColor(color: Color | undefined, fallback: [number, number, number]): [number, number, number] {
  return color ? [color.r, color.g, color.b] : fallback;
}

function stableId(prefix: string, uuid: string, fallback: number): string {
  return `${prefix}_${uuid ? uuid.replaceAll("-", "") : fallback}`;
}

function safeJson(value: unknown): string {
  try {
    return JSON.stringify(value ?? {});
  } catch {
    return "{}";
  }
}

async function encodeTexture(texture: Texture, warnings: string[]): Promise<Pick<ThreeUnityTexture, "width" | "height" | "encoding" | "data">> {
  const image = texture.image as { width?: number; height?: number; data?: ArrayLike<number>; toDataURL?: (type?: string) => string } | undefined;
  const width = image?.width ?? 0;
  const height = image?.height ?? 0;
  if (image?.data && width > 0 && height > 0) {
    if (!(image.data instanceof Uint8Array) && !(image.data instanceof Uint8ClampedArray)) {
      warnings.push(`${texture.name || texture.uuid}: only unsigned-byte DataTexture is supported in format v2.`);
      return { width, height, encoding: "rgba8", data: "" };
    }
    const bytes = image.data instanceof Uint8Array ? image.data : new Uint8Array(image.data);
    if (bytes.length !== width * height * 4) {
      warnings.push(`${texture.name || texture.uuid}: only 4-channel Uint8 DataTexture is supported in format v2.`);
      return { width, height, encoding: "rgba8", data: "" };
    }
    return { width, height, encoding: "rgba8", data: bytesToBase64(bytes) };
  }
  if (image?.toDataURL) return { width, height, encoding: "data-url", data: image.toDataURL("image/png") };
  if (typeof document !== "undefined" && image && width > 0 && height > 0) {
    try {
      const canvas = document.createElement("canvas");
      canvas.width = width;
      canvas.height = height;
      const context = canvas.getContext("2d");
      if (context) {
        context.drawImage(image as unknown as CanvasImageSource, 0, 0);
        return { width, height, encoding: "data-url", data: canvas.toDataURL("image/png") };
      }
    } catch {
      // Cross-origin browser images commonly land here; report it as an export warning below.
    }
  }
  warnings.push(`${texture.name || texture.uuid}: texture image could not be embedded.`);
  return { width, height, encoding: "rgba8", data: "" };
}

function bytesToBase64(bytes: Uint8Array): string {
  if (typeof Buffer !== "undefined") return Buffer.from(bytes).toString("base64");
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

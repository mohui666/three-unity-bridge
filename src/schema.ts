export const THREE_UNITY_FORMAT = "three-unity-scene" as const;
export const THREE_UNITY_LEGACY_VERSION = 1 as const;
export const THREE_UNITY_VERSION = 2 as const;

export type Vec2 = [number, number];
export type Vec3 = [number, number, number];
export type Vec4 = [number, number, number, number];

export interface ThreeUnityDocument {
  format: typeof THREE_UNITY_FORMAT;
  version: typeof THREE_UNITY_VERSION;
  generator: string;
  name: string;
  coordinateSystem: "threejs-right-handed-y-up";
  unitScaleMeters: number;
  nodes: ThreeUnityNode[];
  meshes: ThreeUnityMesh[];
  skins: ThreeUnitySkin[];
  animations: ThreeUnityAnimation[];
  defaultAnimationId: string;
  autoplayAnimation: boolean;
  materials: ThreeUnityMaterial[];
  textures: ThreeUnityTexture[];
  runtime: ThreeUnityRuntime;
  warnings: string[];
}

export type ThreeUnityController = "none" | "first-person" | "orbit";
export type ThreeUnityColliderMode = "none" | "mesh" | "box-per-mesh" | "box-per-24-vertex-mesh";

export interface ThreeUnityRuntime {
  controller: ThreeUnityController;
  colliderMode: ThreeUnityColliderMode;
  enableBlockEditing: boolean;
  allowFly: boolean;
  hudStyle: "diagnostic" | "voxel-hotbar" | "none";
  moveSpeed: number;
  sprintSpeed: number;
  flySpeed: number;
  hotbar: ThreeUnityHotbarItem[];
}

export interface ThreeUnityHotbarItem {
  name: string;
  color: Vec4;
}

export interface ThreeUnityNode {
  id: string;
  name: string;
  parentId: string;
  visible: boolean;
  position: Vec3;
  quaternion: Vec4;
  scale: Vec3;
  layersMask: number;
  meshId: string;
  skinId: string;
  camera?: ThreeUnityCamera;
  light?: ThreeUnityLight;
  metadataJson: string;
  components: ThreeUnityComponent[];
}

export interface ThreeUnityCamera {
  type: "perspective" | "orthographic";
  fov: number;
  near: number;
  far: number;
  aspect: number;
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface ThreeUnityLight {
  type: "directional" | "point" | "spot" | "ambient";
  color: Vec3;
  intensity: number;
  range: number;
  spotAngleRadians: number;
  penumbra: number;
  castShadow: boolean;
}

export interface ThreeUnityComponent {
  type: string;
  dataJson: string;
}

export interface ThreeUnityMesh {
  id: string;
  name: string;
  positions: number[];
  normals: number[];
  uv0: number[];
  colors: number[];
  indices: number[];
  groups: ThreeUnityMeshGroup[];
  materialIds: string[];
  skinIndices: number[];
  skinWeights: number[];
}

export interface ThreeUnitySkin {
  id: string;
  name: string;
  meshNodeId: string;
  boneNodeIds: string[];
  rootBoneNodeId: string;
  /** Three.js Skeleton.boneInverses, flattened in bone order. */
  inverseBindMatrices: number[];
  /** Three.js SkinnedMesh.bindMatrix. */
  bindMatrix: number[];
}

export type ThreeUnityAnimationProperty = "position" | "quaternion" | "scale";

export interface ThreeUnityAnimationTrack {
  targetNodeId: string;
  property: ThreeUnityAnimationProperty;
  times: number[];
  values: number[];
  interpolation: "linear";
  baked: true;
}

export interface ThreeUnityAnimation {
  id: string;
  name: string;
  duration: number;
  loop: boolean;
  tracks: ThreeUnityAnimationTrack[];
}

export interface ThreeUnityMeshGroup {
  start: number;
  count: number;
  materialIndex: number;
}

export interface ThreeUnityMaterial {
  id: string;
  name: string;
  sourceType: string;
  baseColor: Vec4;
  emissive: Vec3;
  metallic: number;
  roughness: number;
  opacity: number;
  transparent: boolean;
  doubleSided: boolean;
  alphaCutoff: number;
  unlit: boolean;
  vertexColors: boolean;
  baseColorTextureId: string;
  emissiveTextureId: string;
  normalTextureId: string;
  metallicRoughnessTextureId: string;
}

export interface ThreeUnityTexture {
  id: string;
  name: string;
  width: number;
  height: number;
  encoding: "rgba8" | "data-url";
  data: string;
  flipY: boolean;
  colorSpace: string;
}

export interface ValidationResult {
  valid: boolean;
  errors: string[];
}

export function validateDocument(value: unknown): ValidationResult {
  const errors: string[] = [];
  if (typeof value !== "object" || value === null) {
    return { valid: false, errors: ["Document must be an object."] };
  }

  const document = value as Partial<ThreeUnityDocument>;
  if (document.format !== THREE_UNITY_FORMAT) errors.push(`format must be '${THREE_UNITY_FORMAT}'.`);
  const version = (document as { version?: unknown }).version;
  if (version !== THREE_UNITY_LEGACY_VERSION && version !== THREE_UNITY_VERSION) {
    errors.push(`version must be ${THREE_UNITY_LEGACY_VERSION} or ${THREE_UNITY_VERSION}.`);
  }
  if (!Array.isArray(document.nodes)) errors.push("nodes must be an array.");
  if (!Array.isArray(document.meshes)) errors.push("meshes must be an array.");
  if (!Array.isArray(document.materials)) errors.push("materials must be an array.");
  if (!Array.isArray(document.textures)) errors.push("textures must be an array.");
  // Early format-v1 exporters predate runtime profiles. Unity already imports
  // those documents with the same safe "none" defaults used by the current
  // exporter, so keep validation backward compatible with checked-in v1 assets.
  // An explicitly present null/non-object value is still malformed.
  if (document.runtime !== undefined && (document.runtime === null || typeof document.runtime !== "object")) {
    errors.push("runtime must be an object when present.");
  }

  const v2 = version === THREE_UNITY_VERSION;
  const documentV2 = document as Partial<ThreeUnityDocument>;
  if (v2) {
    if (!Array.isArray(documentV2.skins)) errors.push("skins must be an array in version 2.");
    if (!Array.isArray(documentV2.animations)) errors.push("animations must be an array in version 2.");
    if (typeof documentV2.defaultAnimationId !== "string") errors.push("defaultAnimationId must be a string in version 2.");
    if (typeof documentV2.autoplayAnimation !== "boolean") errors.push("autoplayAnimation must be a boolean in version 2.");
  }

  const nodeIds = new Set<string>();
  const nodesById = new Map<string, ThreeUnityNode>();
  if (Array.isArray(document.nodes)) {
    for (const [index, node] of document.nodes.entries()) {
      if (!node.id) errors.push(`nodes[${index}].id is required.`);
      if (nodeIds.has(node.id)) errors.push(`Duplicate node id '${node.id}'.`);
      nodeIds.add(node.id);
      nodesById.set(node.id, node);
      if (!Array.isArray(node.position) || node.position.length !== 3) errors.push(`nodes[${index}].position must have 3 values.`);
      if (!Array.isArray(node.quaternion) || node.quaternion.length !== 4) errors.push(`nodes[${index}].quaternion must have 4 values.`);
      if (!Array.isArray(node.scale) || node.scale.length !== 3) errors.push(`nodes[${index}].scale must have 3 values.`);
      if (v2 && typeof node.skinId !== "string") errors.push(`nodes[${index}].skinId must be a string in version 2.`);
    }
    for (const node of document.nodes) {
      if (node.parentId && !nodeIds.has(node.parentId)) errors.push(`Node '${node.id}' references missing parent '${node.parentId}'.`);
    }
  }

  const meshesById = new Map<string, ThreeUnityMesh>();
  if (Array.isArray(document.meshes)) {
    for (const [index, mesh] of document.meshes.entries()) {
      if (!mesh.id) errors.push(`meshes[${index}].id is required.`);
      if (meshesById.has(mesh.id)) errors.push(`Duplicate mesh id '${mesh.id}'.`);
      meshesById.set(mesh.id, mesh);
      if (v2 && !Array.isArray(mesh.skinIndices)) errors.push(`meshes[${index}].skinIndices must be an array in version 2.`);
      if (v2 && !Array.isArray(mesh.skinWeights)) errors.push(`meshes[${index}].skinWeights must be an array in version 2.`);
    }
  }

  for (const node of nodesById.values()) {
    if (node.meshId && !meshesById.has(node.meshId)) errors.push(`Node '${node.id}' references missing mesh '${node.meshId}'.`);
  }

  const skinIds = new Set<string>();
  if (Array.isArray(documentV2.skins)) {
    for (const [index, skin] of documentV2.skins.entries()) {
      const path = `skins[${index}]`;
      if (!skin.id) errors.push(`${path}.id is required.`);
      if (skinIds.has(skin.id)) errors.push(`Duplicate skin id '${skin.id}'.`);
      skinIds.add(skin.id);
      const meshNode = nodesById.get(skin.meshNodeId);
      if (!meshNode) errors.push(`${path} references missing mesh node '${skin.meshNodeId}'.`);
      else {
        if (!meshNode.meshId) errors.push(`${path} mesh node '${meshNode.id}' must reference a mesh.`);
        if (meshNode.skinId !== skin.id) errors.push(`Node '${meshNode.id}' must reference skin '${skin.id}'.`);
      }
      if (!skin.rootBoneNodeId || !nodeIds.has(skin.rootBoneNodeId)) errors.push(`${path} references missing root bone '${skin.rootBoneNodeId}'.`);
      if (!Array.isArray(skin.boneNodeIds) || skin.boneNodeIds.length === 0) errors.push(`${path}.boneNodeIds must contain at least one node id.`);
      else {
        for (const boneNodeId of skin.boneNodeIds) {
          if (!nodeIds.has(boneNodeId)) errors.push(`${path} references missing bone node '${boneNodeId}'.`);
        }
      }
      const boneCount = Array.isArray(skin.boneNodeIds) ? skin.boneNodeIds.length : 0;
      if (!isFiniteNumberArray(skin.inverseBindMatrices) || skin.inverseBindMatrices.length !== boneCount * 16) {
        errors.push(`${path}.inverseBindMatrices must contain 16 finite values per bone.`);
      }
      if (!isFiniteNumberArray(skin.bindMatrix) || skin.bindMatrix.length !== 16) {
        errors.push(`${path}.bindMatrix must contain 16 finite values.`);
      }

      const mesh = meshNode ? meshesById.get(meshNode.meshId) : undefined;
      if (mesh) validateSkinWeights(mesh, boneCount, path, errors);
    }
  }

  for (const node of nodesById.values()) {
    if (node.skinId && !skinIds.has(node.skinId)) errors.push(`Node '${node.id}' references missing skin '${node.skinId}'.`);
  }

  const animationIds = new Set<string>();
  if (Array.isArray(documentV2.animations)) {
    for (const [animationIndex, animation] of documentV2.animations.entries()) {
      const path = `animations[${animationIndex}]`;
      if (!animation.id) errors.push(`${path}.id is required.`);
      if (animationIds.has(animation.id)) errors.push(`Duplicate animation id '${animation.id}'.`);
      animationIds.add(animation.id);
      if (!Number.isFinite(animation.duration) || animation.duration <= 0) errors.push(`${path}.duration must be a positive finite number.`);
      if (typeof animation.loop !== "boolean") errors.push(`${path}.loop must be a boolean.`);
      if (!Array.isArray(animation.tracks)) {
        errors.push(`${path}.tracks must be an array.`);
        continue;
      }
      for (const [trackIndex, track] of animation.tracks.entries()) validateAnimationTrack(track, `${path}.tracks[${trackIndex}]`, nodeIds, errors);
    }
  }

  if (typeof documentV2.defaultAnimationId === "string" && documentV2.defaultAnimationId && !animationIds.has(documentV2.defaultAnimationId)) {
    errors.push(`defaultAnimationId references missing animation '${documentV2.defaultAnimationId}'.`);
  }
  if (documentV2.autoplayAnimation === true && !documentV2.defaultAnimationId) {
    errors.push("autoplayAnimation requires defaultAnimationId.");
  }

  return { valid: errors.length === 0, errors };
}

function validateSkinWeights(mesh: ThreeUnityMesh, boneCount: number, path: string, errors: string[]): void {
  if (!Array.isArray(mesh.positions) || !Array.isArray(mesh.skinIndices) || !Array.isArray(mesh.skinWeights)) {
    errors.push(`${path} mesh '${mesh.id}' must provide positions, skinIndices, and skinWeights arrays.`);
    return;
  }
  const vertexCount = mesh.positions.length / 3;
  const expectedCount = vertexCount * 4;
  if (!Number.isInteger(vertexCount) || mesh.skinIndices.length !== expectedCount || mesh.skinWeights.length !== expectedCount) {
    errors.push(`${path} mesh '${mesh.id}' must contain exactly four skin indices and weights per vertex.`);
    return;
  }
  for (let vertex = 0; vertex < vertexCount; vertex += 1) {
    let sum = 0;
    for (let influence = 0; influence < 4; influence += 1) {
      const offset = vertex * 4 + influence;
      const boneIndex = mesh.skinIndices[offset];
      const weight = mesh.skinWeights[offset];
      if (!Number.isInteger(boneIndex) || boneIndex < 0 || boneIndex >= boneCount) {
        errors.push(`${path} mesh '${mesh.id}' vertex ${vertex} has invalid bone index '${boneIndex}'.`);
      }
      if (!Number.isFinite(weight) || weight < 0) errors.push(`${path} mesh '${mesh.id}' vertex ${vertex} has invalid weight '${weight}'.`);
      else sum += weight;
    }
    if (Math.abs(sum - 1) > 1e-5) errors.push(`${path} mesh '${mesh.id}' vertex ${vertex} skin weights must sum to 1.`);
  }
}

function validateAnimationTrack(track: ThreeUnityAnimationTrack, path: string, nodeIds: Set<string>, errors: string[]): void {
  if (!nodeIds.has(track.targetNodeId)) errors.push(`${path} references missing target node '${track.targetNodeId}'.`);
  const dimensions = track.property === "quaternion" ? 4 : track.property === "position" || track.property === "scale" ? 3 : 0;
  if (dimensions === 0) errors.push(`${path}.property must be position, quaternion, or scale.`);
  const validTimes = isFiniteNumberArray(track.times) && track.times.length > 0;
  if (!validTimes) errors.push(`${path}.times must contain finite values.`);
  else {
    for (let index = 1; index < track.times.length; index += 1) {
      if (track.times[index] <= track.times[index - 1]) errors.push(`${path}.times must be strictly increasing.`);
    }
  }
  if (!isFiniteNumberArray(track.values) || dimensions > 0 && validTimes && track.values.length !== track.times.length * dimensions) {
    errors.push(`${path}.values length must match times and property dimensions.`);
  }
  if (track.interpolation !== "linear") errors.push(`${path}.interpolation must be 'linear'.`);
  if (track.baked !== true) errors.push(`${path}.baked must be true.`);
}

function isFiniteNumberArray(value: unknown): value is number[] {
  return Array.isArray(value) && value.every((item) => typeof item === "number" && Number.isFinite(item));
}

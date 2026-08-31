export const THREE_UNITY_FORMAT = "three-unity-scene" as const;
export const THREE_UNITY_LEGACY_VERSION = 1 as const;
export const THREE_UNITY_SKINNED_VERSION = 2 as const;
export const THREE_UNITY_MORPH_VERSION = 3 as const;
export const THREE_UNITY_MATERIAL_ANIMATION_VERSION = 4 as const;
export const THREE_UNITY_VERSION = 5 as const;

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
  primitives: ThreeUnityPrimitive[];
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
  primitiveId: string;
  skinId: string;
  morphWeights: number[];
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
  morphTargets: ThreeUnityMorphTarget[];
}

export type ThreeUnityPrimitiveType = "line" | "line-segments" | "line-loop" | "points" | "sprite";

export interface ThreeUnityPrimitive {
  id: string;
  name: string;
  type: ThreeUnityPrimitiveType;
  positions: number[];
  colors: number[];
  indices: number[];
  groups: ThreeUnityMeshGroup[];
  materialIds: string[];
  spriteCenter: Vec2;
}

export interface ThreeUnityMorphTarget {
  name: string;
  positionDeltas: number[];
  normalDeltas: number[];
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

export type ThreeUnityAnimationProperty =
  | "position"
  | "quaternion"
  | "scale"
  | "morphWeight"
  | "materialBaseColor"
  | "materialEmissive"
  | "materialMetallic"
  | "materialRoughness"
  | "materialBaseMapST";

export interface ThreeUnityAnimationTrack {
  targetNodeId: string;
  property: ThreeUnityAnimationProperty;
  morphTargetIndex: number;
  materialIndex: number;
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

export type ThreeUnityMaterialRenderMode = "surface" | "line" | "points" | "sprite";

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
  baseColorTextureST: Vec4;
  emissiveTextureId: string;
  normalTextureId: string;
  metallicRoughnessTextureId: string;
  renderMode: ThreeUnityMaterialRenderMode;
  pointSize: number;
  sizeAttenuation: boolean;
  spriteRotation: number;
}

export type ThreeUnityTextureWrap = "repeat" | "clamp" | "mirror";

export interface ThreeUnityTexture {
  id: string;
  name: string;
  width: number;
  height: number;
  encoding: "rgba8" | "data-url";
  data: string;
  flipY: boolean;
  colorSpace: string;
  wrapS: ThreeUnityTextureWrap;
  wrapT: ThreeUnityTextureWrap;
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
  if (
    version !== THREE_UNITY_LEGACY_VERSION
    && version !== THREE_UNITY_SKINNED_VERSION
    && version !== THREE_UNITY_MORPH_VERSION
    && version !== THREE_UNITY_MATERIAL_ANIMATION_VERSION
    && version !== THREE_UNITY_VERSION
  ) {
    errors.push(
      `version must be ${THREE_UNITY_LEGACY_VERSION}, ${THREE_UNITY_SKINNED_VERSION}, ${THREE_UNITY_MORPH_VERSION}, ${THREE_UNITY_MATERIAL_ANIMATION_VERSION}, or ${THREE_UNITY_VERSION}.`,
    );
  }
  if (!Array.isArray(document.nodes)) errors.push("nodes must be an array.");
  if (!Array.isArray(document.meshes)) errors.push("meshes must be an array.");
  if (version === THREE_UNITY_VERSION && !Array.isArray(document.primitives)) errors.push("primitives must be an array in version 5.");
  if (!Array.isArray(document.materials)) errors.push("materials must be an array.");
  if (!Array.isArray(document.textures)) errors.push("textures must be an array.");
  // Early format-v1 exporters predate runtime profiles. Unity already imports
  // those documents with the same safe "none" defaults used by the current
  // exporter, so keep validation backward compatible with checked-in v1 assets.
  // An explicitly present null/non-object value is still malformed.
  if (document.runtime !== undefined && (document.runtime === null || typeof document.runtime !== "object")) {
    errors.push("runtime must be an object when present.");
  }

  const v2OrLater = version === THREE_UNITY_SKINNED_VERSION
    || version === THREE_UNITY_MORPH_VERSION
    || version === THREE_UNITY_MATERIAL_ANIMATION_VERSION
    || version === THREE_UNITY_VERSION;
  const v3OrLater = version === THREE_UNITY_MORPH_VERSION
    || version === THREE_UNITY_MATERIAL_ANIMATION_VERSION
    || version === THREE_UNITY_VERSION;
  const v4OrLater = version === THREE_UNITY_MATERIAL_ANIMATION_VERSION || version === THREE_UNITY_VERSION;
  const v5 = version === THREE_UNITY_VERSION;
  const documentV2 = document as Partial<ThreeUnityDocument>;
  if (v2OrLater) {
    if (!Array.isArray(documentV2.skins)) errors.push("skins must be an array in version 2 or later.");
    if (!Array.isArray(documentV2.animations)) errors.push("animations must be an array in version 2 or later.");
    if (typeof documentV2.defaultAnimationId !== "string") errors.push("defaultAnimationId must be a string in version 2 or later.");
    if (typeof documentV2.autoplayAnimation !== "boolean") errors.push("autoplayAnimation must be a boolean in version 2 or later.");
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
      if (v2OrLater && typeof node.skinId !== "string") errors.push(`nodes[${index}].skinId must be a string in version 2 or later.`);
      if (v3OrLater && !isFiniteNumberArray(node.morphWeights)) errors.push(`nodes[${index}].morphWeights must contain finite values in version 3 or later.`);
      if (v5 && typeof node.primitiveId !== "string") errors.push(`nodes[${index}].primitiveId must be a string in version 5.`);
      if (v5 && node.meshId && node.primitiveId) errors.push(`nodes[${index}] cannot reference both meshId and primitiveId.`);
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
      if (v2OrLater && !Array.isArray(mesh.skinIndices)) errors.push(`meshes[${index}].skinIndices must be an array in version 2 or later.`);
      if (v2OrLater && !Array.isArray(mesh.skinWeights)) errors.push(`meshes[${index}].skinWeights must be an array in version 2 or later.`);
      if (v3OrLater) validateMorphTargets(mesh, `meshes[${index}]`, errors);
    }
  }

  const materialsById = new Map<string, ThreeUnityMaterial>();
  const textureIds = new Set<string>();
  if (v4OrLater && Array.isArray(document.textures)) {
    for (const [index, texture] of document.textures.entries()) {
      const path = `textures[${index}]`;
      if (!texture.id) errors.push(`${path}.id is required.`);
      if (textureIds.has(texture.id)) errors.push(`Duplicate texture id '${texture.id}'.`);
      textureIds.add(texture.id);
      if (!isTextureWrap(texture.wrapS)) errors.push(`${path}.wrapS must be repeat, clamp, or mirror.`);
      if (!isTextureWrap(texture.wrapT)) errors.push(`${path}.wrapT must be repeat, clamp, or mirror.`);
    }
  }
  if (v4OrLater && Array.isArray(document.materials)) {
    for (const [index, material] of document.materials.entries()) {
      const path = `materials[${index}]`;
      if (!material.id) errors.push(`${path}.id is required.`);
      if (materialsById.has(material.id)) errors.push(`Duplicate material id '${material.id}'.`);
      materialsById.set(material.id, material);
      if (!isFiniteNumberArray(material.baseColorTextureST) || material.baseColorTextureST.length !== 4) {
        errors.push(`${path}.baseColorTextureST must contain 4 finite values.`);
      }
      if (v5) {
        if (!isMaterialRenderMode(material.renderMode)) errors.push(`${path}.renderMode must be surface, line, points, or sprite.`);
        if (material.renderMode === "points" && (!Number.isFinite(material.pointSize) || material.pointSize <= 0)) {
          errors.push(`${path}.pointSize must be a positive finite number for points materials.`);
        }
        if (typeof material.sizeAttenuation !== "boolean") errors.push(`${path}.sizeAttenuation must be a boolean.`);
        if (!Number.isFinite(material.spriteRotation)) errors.push(`${path}.spriteRotation must be finite.`);
      }
    }
  }

  const primitivesById = new Map<string, ThreeUnityPrimitive>();
  if (v5 && Array.isArray(document.primitives)) {
    for (const [index, primitive] of document.primitives.entries()) {
      const path = `primitives[${index}]`;
      if (!primitive.id) errors.push(`${path}.id is required.`);
      if (primitivesById.has(primitive.id)) errors.push(`Duplicate primitive id '${primitive.id}'.`);
      primitivesById.set(primitive.id, primitive);
      validatePrimitive(primitive, path, materialsById, errors);
    }
  }

  for (const node of nodesById.values()) {
    if (node.meshId && !meshesById.has(node.meshId)) errors.push(`Node '${node.id}' references missing mesh '${node.meshId}'.`);
    if (v5 && node.primitiveId && !primitivesById.has(node.primitiveId)) {
      errors.push(`Node '${node.id}' references missing primitive '${node.primitiveId}'.`);
    }
    if (v3OrLater && Array.isArray(node.morphWeights)) {
      const expectedCount = node.meshId ? meshesById.get(node.meshId)?.morphTargets?.length ?? 0 : 0;
      if (node.morphWeights.length !== expectedCount) {
        errors.push(`Node '${node.id}' morphWeights length must match its mesh morph target count (${expectedCount}).`);
      }
    }
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
      for (const [trackIndex, track] of animation.tracks.entries()) {
        validateAnimationTrack(
          track,
          `${path}.tracks[${trackIndex}]`,
          nodesById,
          meshesById,
          primitivesById,
          materialsById,
          v3OrLater,
          v4OrLater,
          errors,
        );
      }
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

function validatePrimitive(
  primitive: ThreeUnityPrimitive,
  path: string,
  materialsById: Map<string, ThreeUnityMaterial>,
  errors: string[],
): void {
  if (!isPrimitiveType(primitive.type)) errors.push(`${path}.type must be line, line-segments, line-loop, points, or sprite.`);
  const validPositions = isFiniteNumberArray(primitive.positions) && primitive.positions.length % 3 === 0;
  if (!validPositions) errors.push(`${path}.positions must contain 3 finite values per vertex.`);
  const vertexCount = validPositions ? primitive.positions.length / 3 : 0;
  if (!isFiniteNumberArray(primitive.colors) || primitive.colors.length !== 0 && primitive.colors.length !== vertexCount * 4) {
    errors.push(`${path}.colors must be empty or contain 4 finite values per vertex.`);
  }
  if (!isFiniteNumberArray(primitive.indices)) {
    errors.push(`${path}.indices must contain finite values.`);
  } else {
    for (const [index, vertexIndex] of primitive.indices.entries()) {
      if (!Number.isInteger(vertexIndex) || vertexIndex < 0 || vertexIndex >= vertexCount) {
        errors.push(`${path}.indices[${index}] must be a non-negative integer below vertex count ${vertexCount}.`);
      }
    }
  }
  if (!Array.isArray(primitive.materialIds)) {
    errors.push(`${path}.materialIds must be an array.`);
  } else {
    for (const materialId of primitive.materialIds) {
      if (!materialsById.has(materialId)) errors.push(`${path} references missing material '${materialId}'.`);
    }
  }
  if (!Array.isArray(primitive.groups)) {
    errors.push(`${path}.groups must be an array.`);
  } else {
    const materialCount = Array.isArray(primitive.materialIds) ? primitive.materialIds.length : 0;
    const indexCount = Array.isArray(primitive.indices) ? primitive.indices.length : 0;
    for (const [groupIndex, group] of primitive.groups.entries()) {
      const groupPath = `${path}.groups[${groupIndex}]`;
      if (!Number.isInteger(group.materialIndex) || group.materialIndex < 0 || group.materialIndex >= materialCount) {
        errors.push(`${groupPath}.materialIndex must reference primitive materialIds.`);
      }
      if (!Number.isInteger(group.start) || group.start < 0 || !Number.isInteger(group.count) || group.count < 0 || group.start + group.count > indexCount) {
        errors.push(`${groupPath}.start/count must be non-negative integers within canonical indices.`);
      }
    }
  }
  if (!isFiniteNumberArray(primitive.spriteCenter) || primitive.spriteCenter.length !== 2) {
    errors.push(`${path}.spriteCenter must contain 2 finite values.`);
  }

  const line = primitive.type === "line" || primitive.type === "line-segments" || primitive.type === "line-loop";
  if (line) {
    if (vertexCount === 0) errors.push(`${path}.positions must not be empty for line primitives.`);
    if (!Array.isArray(primitive.materialIds) || primitive.materialIds.length === 0) errors.push(`${path}.materialIds must contain a line material.`);
    if (Array.isArray(primitive.indices) && primitive.indices.length % 2 !== 0) {
      errors.push(`${path}.indices must contain explicit line segment pairs.`);
    }
  } else if (primitive.type === "points") {
    if (vertexCount === 0) errors.push(`${path}.positions must not be empty for points primitives.`);
    if (!Array.isArray(primitive.materialIds) || primitive.materialIds.length === 0) errors.push(`${path}.materialIds must contain a points material.`);
  } else if (primitive.type === "sprite") {
    if (primitive.positions?.length !== 0 || primitive.colors?.length !== 0 || primitive.indices?.length !== 0 || primitive.groups?.length !== 0) {
      errors.push(`${path} sprite data arrays and groups must be empty.`);
    }
    if (!Array.isArray(primitive.materialIds) || primitive.materialIds.length !== 1) {
      errors.push(`${path}.materialIds must contain exactly one sprite material.`);
    }
  }

  const expectedRenderMode = line ? "line" : primitive.type === "points" ? "points" : primitive.type === "sprite" ? "sprite" : undefined;
  if (expectedRenderMode && Array.isArray(primitive.materialIds)) {
    for (const materialId of primitive.materialIds) {
      const material = materialsById.get(materialId);
      if (material && material.renderMode !== expectedRenderMode) {
        errors.push(`${path} material '${materialId}' renderMode must be '${expectedRenderMode}'.`);
      }
    }
  }
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

function validateMorphTargets(mesh: ThreeUnityMesh, path: string, errors: string[]): void {
  if (!Array.isArray(mesh.morphTargets)) {
    errors.push(`${path}.morphTargets must be an array in version 3.`);
    return;
  }
  const vertexCount = Array.isArray(mesh.positions) ? mesh.positions.length / 3 : Number.NaN;
  const expectedDeltaCount = Number.isInteger(vertexCount) ? vertexCount * 3 : -1;
  const names = new Set<string>();
  for (const [targetIndex, target] of mesh.morphTargets.entries()) {
    const targetPath = `${path}.morphTargets[${targetIndex}]`;
    if (typeof target.name !== "string" || target.name.length === 0) errors.push(`${targetPath}.name is required.`);
    else if (names.has(target.name)) errors.push(`${path} has duplicate morph target name '${target.name}'.`);
    else names.add(target.name);
    if (!isFiniteNumberArray(target.positionDeltas) || target.positionDeltas.length !== expectedDeltaCount) {
      errors.push(`${targetPath}.positionDeltas must contain 3 finite values per vertex.`);
    }
    if (!isFiniteNumberArray(target.normalDeltas) || target.normalDeltas.length !== 0 && target.normalDeltas.length !== expectedDeltaCount) {
      errors.push(`${targetPath}.normalDeltas must be empty or contain 3 finite values per vertex.`);
    }
  }
}

function validateAnimationTrack(
  track: ThreeUnityAnimationTrack,
  path: string,
  nodesById: Map<string, ThreeUnityNode>,
  meshesById: Map<string, ThreeUnityMesh>,
  primitivesById: Map<string, ThreeUnityPrimitive>,
  materialsById: Map<string, ThreeUnityMaterial>,
  v3OrLater: boolean,
  v4OrLater: boolean,
  errors: string[],
): void {
  const targetNode = nodesById.get(track.targetNodeId);
  if (!targetNode) errors.push(`${path} references missing target node '${track.targetNodeId}'.`);
  const transformDimensions = track.property === "quaternion"
    ? 4
    : track.property === "position" || track.property === "scale"
      ? 3
      : 0;
  const materialDimensions = v4OrLater ? materialAnimationDimensions(track.property) : 0;
  const dimensions = transformDimensions || (v3OrLater && track.property === "morphWeight" ? 1 : 0) || materialDimensions;
  if (dimensions === 0) {
    const allowed = ["position", "quaternion", "scale"];
    if (v3OrLater) allowed.push("morphWeight");
    if (v4OrLater) allowed.push("materialBaseColor", "materialEmissive", "materialMetallic", "materialRoughness", "materialBaseMapST");
    errors.push(`${path}.property must be ${allowed.join(", ")}.`);
  }

  if (v3OrLater) {
    if (!Number.isInteger(track.morphTargetIndex)) errors.push(`${path}.morphTargetIndex must be an integer.`);
    else if (track.property === "morphWeight") {
      const mesh = targetNode?.meshId ? meshesById.get(targetNode.meshId) : undefined;
      if (!mesh) errors.push(`${path} morphWeight target node '${track.targetNodeId}' must reference a mesh.`);
      else if (!Array.isArray(mesh.morphTargets) || track.morphTargetIndex < 0 || track.morphTargetIndex >= mesh.morphTargets.length) {
        errors.push(`${path}.morphTargetIndex '${track.morphTargetIndex}' is out of range for mesh '${mesh.id}'.`);
      }
    } else if (track.morphTargetIndex !== -1) {
      errors.push(`${path}.morphTargetIndex must be -1 for non-morph tracks.`);
    }
  }

  if (v4OrLater) {
    if (materialDimensions > 0) {
      if (!Number.isInteger(track.materialIndex) || track.materialIndex < 0) {
        errors.push(`${path}.materialIndex must be a non-negative integer for material tracks.`);
      } else {
        const mesh = targetNode?.meshId ? meshesById.get(targetNode.meshId) : undefined;
        const primitive = targetNode?.primitiveId ? primitivesById.get(targetNode.primitiveId) : undefined;
        const renderable = mesh ?? primitive;
        if (!renderable) {
          errors.push(`${path} material target node '${track.targetNodeId}' must reference a mesh or primitive.`);
        } else if (!Array.isArray(renderable.materialIds) || track.materialIndex >= renderable.materialIds.length) {
          errors.push(`${path}.materialIndex '${track.materialIndex}' is out of range for renderable '${renderable.id}'.`);
        } else {
          const materialId = renderable.materialIds[track.materialIndex];
          const material = materialsById.get(materialId);
          if (!material) errors.push(`${path} references missing material '${materialId}'.`);
          else if (track.property === "materialBaseMapST" && !material.baseColorTextureId) {
            errors.push(`${path} materialBaseMapST target material '${materialId}' has no base color texture.`);
          } else if (primitive && !isPrimitiveMaterialAnimationProperty(material.renderMode, track.property)) {
            errors.push(`${path} property '${track.property}' is not supported by ${material.renderMode} material '${materialId}'.`);
          }
          const groups = Array.isArray(renderable.groups) ? renderable.groups : [];
          const sourceIndexIsUsed = groups.length > 0
            ? groups.some((group) => group.materialIndex === track.materialIndex)
            : track.materialIndex === 0;
          if (!sourceIndexIsUsed) {
            errors.push(`${path}.materialIndex '${track.materialIndex}' is not used by renderable '${renderable.id}' groups.`);
          }
        }
      }
    } else if (track.materialIndex !== -1) {
      errors.push(`${path}.materialIndex must be -1 for non-material tracks.`);
    }
  }

  const validTimes = isFiniteNumberArray(track.times) && track.times.length > 0;
  if (!validTimes) errors.push(`${path}.times must contain finite values.`);
  else {
    for (let index = 1; index < track.times.length; index += 1) {
      if (track.times[index] < track.times[index - 1]) errors.push(`${path}.times must be monotonically non-decreasing.`);
    }
  }
  if (!isFiniteNumberArray(track.values) || dimensions > 0 && validTimes && track.values.length !== track.times.length * dimensions) {
    errors.push(`${path}.values length must match times and property dimensions.`);
  }
  if (track.interpolation !== "linear") errors.push(`${path}.interpolation must be 'linear'.`);
  if (track.baked !== true) errors.push(`${path}.baked must be true.`);
}

function materialAnimationDimensions(property: ThreeUnityAnimationProperty): number {
  if (property === "materialBaseColor" || property === "materialBaseMapST") return 4;
  if (property === "materialEmissive") return 3;
  if (property === "materialMetallic" || property === "materialRoughness") return 1;
  return 0;
}

function isPrimitiveMaterialAnimationProperty(
  renderMode: ThreeUnityMaterialRenderMode,
  property: ThreeUnityAnimationProperty,
): boolean {
  if (renderMode === "surface") return true;
  if (property === "materialBaseColor") return true;
  return (renderMode === "points" || renderMode === "sprite") && property === "materialBaseMapST";
}

function isPrimitiveType(value: unknown): value is ThreeUnityPrimitiveType {
  return value === "line" || value === "line-segments" || value === "line-loop" || value === "points" || value === "sprite";
}

function isMaterialRenderMode(value: unknown): value is ThreeUnityMaterialRenderMode {
  return value === "surface" || value === "line" || value === "points" || value === "sprite";
}

function isTextureWrap(value: unknown): value is ThreeUnityTextureWrap {
  return value === "repeat" || value === "clamp" || value === "mirror";
}

function isFiniteNumberArray(value: unknown): value is number[] {
  return Array.isArray(value) && value.every((item) => typeof item === "number" && Number.isFinite(item));
}

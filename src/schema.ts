export const THREE_UNITY_FORMAT = "three-unity-scene" as const;
export const THREE_UNITY_VERSION = 1 as const;

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
  if (document.version !== THREE_UNITY_VERSION) errors.push(`version must be ${THREE_UNITY_VERSION}.`);
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

  if (Array.isArray(document.nodes)) {
    const ids = new Set<string>();
    for (const [index, node] of document.nodes.entries()) {
      if (!node.id) errors.push(`nodes[${index}].id is required.`);
      if (ids.has(node.id)) errors.push(`Duplicate node id '${node.id}'.`);
      ids.add(node.id);
      if (!Array.isArray(node.position) || node.position.length !== 3) errors.push(`nodes[${index}].position must have 3 values.`);
      if (!Array.isArray(node.quaternion) || node.quaternion.length !== 4) errors.push(`nodes[${index}].quaternion must have 4 values.`);
      if (!Array.isArray(node.scale) || node.scale.length !== 3) errors.push(`nodes[${index}].scale must have 3 values.`);
    }
    for (const node of document.nodes) {
      if (node.parentId && !ids.has(node.parentId)) errors.push(`Node '${node.id}' references missing parent '${node.parentId}'.`);
    }
  }

  return { valid: errors.length === 0, errors };
}

import {
  BufferAttribute,
  BufferGeometry,
  Camera,
  Color,
  FrontSide,
  Material,
  Mesh,
  MeshStandardMaterial,
  Object3D,
  Quaternion,
  Scene,
  Texture,
  Vector3,
} from "three";
import {
  THREE_UNITY_FORMAT,
  THREE_UNITY_VERSION,
  ThreeUnityCamera,
  ThreeUnityComponent,
  ThreeUnityDocument,
  ThreeUnityLight,
  ThreeUnityMaterial,
  ThreeUnityMesh,
  ThreeUnityNode,
  ThreeUnityRuntime,
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
  const materials: ThreeUnityMaterial[] = [];
  const textures: ThreeUnityTexture[] = [];
  const warnings: string[] = [];
  const warnedRenderableTypes = new Set<string>();
  const meshIds = new Map<string, string>();
  const materialIds = new Map<Material, string>();
  const textureIds = new Map<Texture, string>();
  const visitedObjects = new Set<Object3D>();

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
    // Unity's imported subasset owns its material slots, so meshes that share
    // geometry but override materials must remain separate bridge records.
    const meshKey = `${geometry.uuid}|${meshMaterials.map((material) => material.uuid).join("|")}`;
    const existing = meshIds.get(meshKey);
    if (existing) return existing;
    const id = stableId("mesh", meshKey.replaceAll("|", "_"), meshIds.size);
    meshIds.set(meshKey, id);
    const materialIdList = await Promise.all(meshMaterials.map(registerMaterial));
    const position = requiredAttribute(geometry, "position");
    const normal = geometry.getAttribute("normal") as BufferAttribute | undefined;
    const uv = geometry.getAttribute("uv") as BufferAttribute | undefined;
    const color = geometry.getAttribute("color") as BufferAttribute | undefined;
    if (geometry.morphAttributes.position?.length) warnings.push(`${mesh.name || mesh.uuid}: morph targets are not exported in format v1.`);
    if ((mesh as Mesh & { isSkinnedMesh?: boolean }).isSkinnedMesh) warnings.push(`${mesh.name || mesh.uuid}: skinning is not exported in format v1.`);
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
    });
    return id;
  };

  const visit = async (object: Object3D, parentId: string): Promise<void> => {
    if (visitedObjects.has(object)) return;
    if (!options.includeInvisible && !object.visible) return;
    visitedObjects.add(object);
    const id = stableId("node", object.uuid, nodes.length);
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
      metadataJson: safeJson(metadata),
      components,
    };
    const isInstancedMesh = Boolean((object as Mesh & { isInstancedMesh?: boolean }).isInstancedMesh);
    if ((object as Mesh).isMesh && !isInstancedMesh) node.meshId = await registerMesh(object as Mesh);
    if (!(object as Mesh).isMesh && isUnsupportedRenderable(object)) {
      if (!warnedRenderableTypes.has(object.type)) {
        warnings.push(`${object.type} renderables are preserved as transforms only in format v1.`);
        warnedRenderableTypes.add(object.type);
      }
    }
    if ((object as Camera).isCamera) node.camera = exportCamera(object as Camera);
    if ((object as Object3D & { isLight?: boolean }).isLight) node.light = exportLight(object as Object3D & LightLike);
    nodes.push(node);
    if (isInstancedMesh) await exportInstances(object as Mesh & InstancedMeshLike, id, registerMesh, nodes, warnings);
    for (const child of object.children) await visit(child, id);
  };

  // Export the supplied root as a real node so its transform and metadata survive.
  await visit(root, "");
  for (const object of options.extraObjects ?? []) await visit(object, "");
  return {
    format: THREE_UNITY_FORMAT,
    version: THREE_UNITY_VERSION,
    generator: "three-unity-bridge/0.1.0",
    name: options.name || root.name || "Three.js Scene",
    coordinateSystem: "threejs-right-handed-y-up",
    unitScaleMeters: options.unitScaleMeters ?? 1,
    nodes,
    meshes,
    materials,
    textures,
    runtime: normalizeRuntime(options.runtime),
    warnings: [...new Set(warnings)],
  };
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
      metadataJson: `{\"threeUnityInstance\":${index}}`,
      components: [],
    });
  }
  if (object.instanceColor) warnings.push(`${object.name || object.uuid}: per-instance colors are not exported in format v1.`);
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
      warnings.push(`${texture.name || texture.uuid}: only unsigned-byte DataTexture is supported in format v1.`);
      return { width, height, encoding: "rgba8", data: "" };
    }
    const bytes = image.data instanceof Uint8Array ? image.data : new Uint8Array(image.data);
    if (bytes.length !== width * height * 4) {
      warnings.push(`${texture.name || texture.uuid}: only 4-channel Uint8 DataTexture is supported in format v1.`);
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

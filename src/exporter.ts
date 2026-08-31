import {
  AnimationClip,
  AnimationMixer,
  BufferAttribute,
  BufferGeometry,
  Camera,
  ClampToEdgeWrapping,
  Color,
  FloatType,
  FrontSide,
  HalfFloatType,
  Line,
  LinearFilter,
  LinearMipmapLinearFilter,
  LinearMipmapNearestFilter,
  LinearSRGBColorSpace,
  LoopOnce,
  Material,
  Mesh,
  MeshStandardMaterial,
  MirroredRepeatWrapping,
  NearestFilter,
  NearestMipmapLinearFilter,
  NearestMipmapNearestFilter,
  NoColorSpace,
  Object3D,
  Points,
  PropertyBinding,
  Quaternion,
  RedFormat,
  RepeatWrapping,
  RGFormat,
  RGBFormat,
  RGBAFormat,
  Scene,
  SkinnedMesh,
  Sprite,
  SRGBColorSpace,
  Texture,
  UnsignedByteType,
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
  ThreeUnityInstancedMesh,
  ThreeUnityLight,
  ThreeUnityMaterial,
  ThreeUnityMesh,
  ThreeUnityMorphTarget,
  ThreeUnityNode,
  ThreeUnityPrimitive,
  ThreeUnityPrimitiveType,
  ThreeUnityRuntime,
  ThreeUnitySkin,
  ThreeUnityTexture,
  ThreeUnityTextureColorSpace,
  ThreeUnityTextureComponentType,
  ThreeUnityTextureFilterMode,
  ThreeUnityTextureMimeType,
  ThreeUnityTexturePixelFormat,
  ThreeUnityTextureWrap,
} from "./schema.js";

export interface ThreeUnityTextureResolveRequest {
  texture: Texture;
  sourceUri: string;
}

export interface ThreeUnityResolvedTextureSource {
  bytes: Uint8Array;
  mimeType: "image/png" | "image/jpeg";
}

export type ThreeUnityTextureResolver = (
  request: ThreeUnityTextureResolveRequest,
) => Promise<ThreeUnityResolvedTextureSource | undefined>;

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
  /** Fixed sampling rate used to bake Three.js animation into local transform and morph-weight tracks. */
  animationSampleRate?: number;
  /** Native GPU instancing is the default; expanded preserves one child node per instance. */
  instancedMeshMode?: "gpu" | "expanded";
  /** Reads PNG/JPEG bytes for non-data source URIs without coupling the browser-safe exporter to Node APIs. */
  textureResolver?: ThreeUnityTextureResolver;
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
  const instancedMeshes: ThreeUnityInstancedMesh[] = [];
  const primitives: ThreeUnityPrimitive[] = [];
  const skins: ThreeUnitySkin[] = [];
  const materials: ThreeUnityMaterial[] = [];
  const textures: ThreeUnityTexture[] = [];
  const warnings: string[] = [];
  const meshIds = new Map<string, string>();
  const primitiveIds = new Map<string, string>();
  const morphTargetNamesByMeshId = new Map<string, string[]>();
  const morphTargetNamesByObject = new Map<Object3D, string[]>();
  const skinIds = new Map<SkinnedMesh, string>();
  const materialIds = new Map<Material, string>();
  const textureIds = new Map<Texture, string>();
  const visitedObjects = new Set<Object3D>();
  const exportedMaterialObjects = new Set<Object3D>();
  const gpuInstancedMaterialObjects = new Set<Object3D>();
  const expandedMaterialTargetNodeIds = new Map<Object3D, string[]>();
  const requiredBones = new Set<Object3D>();
  const instancedMeshMode = options.instancedMeshMode ?? "gpu";
  if (instancedMeshMode !== "gpu" && instancedMeshMode !== "expanded") {
    throw new Error(`instancedMeshMode must be 'gpu' or 'expanded', received '${instancedMeshMode}'.`);
  }
  const nodeIdFor = (object: Object3D): string => stableId("node", object.uuid, 0);

  const registerTexture = async (texture: Texture | null | undefined): Promise<string> => {
    if (!texture) return "";
    const existing = textureIds.get(texture);
    if (existing) return existing;
    const id = stableId("texture", texture.uuid, textureIds.size);
    textureIds.set(texture, id);
    const encoded = await encodeTexture(texture, options.textureResolver);
    const filterMode = exportTextureFilterMode(texture, warnings);
    const mipmaps = texture.generateMipmaps;
    if (filterMode === "trilinear" && !mipmaps) {
      throw new Error(`Texture '${texture.name || texture.uuid}' maps to trilinear filtering but generateMipmaps is false.`);
    }
    if (!Number.isFinite(texture.anisotropy) || !Number.isInteger(texture.anisotropy) || texture.anisotropy < 1) {
      throw new Error(`Texture '${texture.name || texture.uuid}' anisotropy must be a finite integer greater than or equal to 1, received '${texture.anisotropy}'.`);
    }
    textures.push({
      id,
      name: texture.name || id,
      ...encoded,
      flipY: texture.flipY,
      colorSpace: exportTextureColorSpace(texture),
      wrapS: exportTextureWrap(texture, "wrapS", texture.wrapS, warnings),
      wrapT: exportTextureWrap(texture, "wrapT", texture.wrapT, warnings),
      filterMode,
      mipmaps,
      anisotropy: texture.anisotropy,
    });
    return id;
  };

  const registerMaterial = async (material: Material): Promise<string> => {
    const existing = materialIds.get(material);
    if (existing) return existing;
    const id = stableId("material", material.uuid, materialIds.size);
    materialIds.set(material, id);
    const standard = material as MeshStandardMaterial;
    const renderMode = materialRenderMode(material);
    if (renderMode === "surface" && !new Set(["MeshBasicMaterial", "MeshStandardMaterial", "MeshPhysicalMaterial", "MeshLambertMaterial", "MeshPhongMaterial"]).has(material.type)) {
      warnings.push(`${material.name || material.uuid}: ${material.type} is approximated with a Unity Lit material.`);
    }
    const primitiveMaterial = material as Material & {
      alphaMap?: Texture | null;
      linewidth?: number;
      map?: Texture | null;
      rotation?: number;
      size?: number;
      sizeAttenuation?: boolean;
    };
    if (renderMode === "line" && primitiveMaterial.linewidth !== undefined && primitiveMaterial.linewidth !== 1) {
      warnings.push(`Material '${material.name || material.uuid}' linewidth '${primitiveMaterial.linewidth}' is not converted; Unity uses 1-pixel lines.`);
    }
    if (renderMode === "line" && primitiveMaterial.map) {
      warnings.push(`Material '${material.name || material.uuid}' LineBasicMaterial.map is not converted.`);
    }
    if ((renderMode === "points" || renderMode === "sprite") && primitiveMaterial.alphaMap) {
      warnings.push(`Material '${material.name || material.uuid}' alphaMap is not converted.`);
    }
    const pointSize = renderMode === "points" ? primitiveMaterial.size ?? 1 : 1;
    if (!Number.isFinite(pointSize) || pointSize <= 0) {
      throw new Error(`PointsMaterial '${material.name || material.uuid}' size must be a positive finite number, received '${pointSize}'.`);
    }
    const spriteRotation = renderMode === "sprite" ? primitiveMaterial.rotation ?? 0 : 0;
    if (!Number.isFinite(spriteRotation)) {
      throw new Error(`SpriteMaterial '${material.name || material.uuid}' rotation must be finite, received '${spriteRotation}'.`);
    }
    const baseColorTexture = renderMode === "line" ? null : primitiveMaterial.map ?? standard.map;
    const baseColor = readColor((material as Material & { color?: Color }).color, [1, 1, 1]);
    const emissive = renderMode === "surface" ? readColor(standard.emissive, [0, 0, 0]) : [0, 0, 0] as [number, number, number];
    const baseColorTextureST = readBaseColorTextureST(material, baseColorTexture, warnings);
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
      doubleSided: renderMode === "surface" ? material.side !== FrontSide : true,
      alphaCutoff: material.alphaTest,
      unlit: renderMode !== "surface" || material.type === "MeshBasicMaterial" || Boolean((material as Material & { isMeshBasicMaterial?: boolean }).isMeshBasicMaterial),
      vertexColors: Boolean((material as Material & { vertexColors?: boolean }).vertexColors),
      baseColorTextureId: await registerTexture(baseColorTexture),
      baseColorTextureST,
      emissiveTextureId: renderMode === "surface" ? await registerTexture(standard.emissiveMap) : "",
      normalTextureId: renderMode === "surface" ? await registerTexture(standard.normalMap) : "",
      metallicRoughnessTextureId: renderMode === "surface" ? await registerTexture(standard.metalnessMap || standard.roughnessMap) : "",
      renderMode,
      pointSize,
      sizeAttenuation: renderMode === "points" || renderMode === "sprite" ? primitiveMaterial.sizeAttenuation ?? true : true,
      spriteRotation,
    };
    materials.push(converted);
    return id;
  };

  const registerMesh = async (mesh: Mesh): Promise<string> => {
    const geometry = mesh.geometry;
    const meshMaterials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    const skinnedMesh = mesh as SkinnedMesh;
    const isSkinnedMesh = Boolean(skinnedMesh.isSkinnedMesh);
    const position = requiredAttribute(geometry, "position");
    const positionMorphAttributes = readMorphAttributes(mesh, "position");
    const normalMorphAttributes = readMorphAttributes(mesh, "normal");
    const morphTargetNames = createMorphTargetNames(mesh, positionMorphAttributes, normalMorphAttributes);
    // Unity's imported subasset owns its material slots, so meshes that share
    // geometry but override materials or morph names must remain separate bridge records.
    const morphNameKey = morphTargetNames.length > 0 ? `|morph:${morphTargetNames.map(encodeURIComponent).join(",")}` : "";
    const meshKey = `${geometry.uuid}|${meshMaterials.map((material) => material.uuid).join("|")}|${isSkinnedMesh ? "skinned" : "static"}${morphNameKey}`;
    const existing = meshIds.get(meshKey);
    if (existing) return existing;
    const id = stableId("mesh", meshKey.replaceAll("|", "_"), meshIds.size);
    meshIds.set(meshKey, id);
    const materialIdList = await Promise.all(meshMaterials.map(registerMaterial));
    const normal = geometry.getAttribute("normal") as BufferAttribute | undefined;
    const uv = geometry.getAttribute("uv") as BufferAttribute | undefined;
    const color = geometry.getAttribute("color") as BufferAttribute | undefined;
    const morphTargets = exportMorphTargets(mesh, position, normal, positionMorphAttributes, normalMorphAttributes, morphTargetNames);
    const skinAttributes = isSkinnedMesh ? normalizeSkinAttributes(skinnedMesh, position) : { skinIndices: [], skinWeights: [] };
    meshes.push({
      id,
      name: geometry.name || mesh.name || id,
      positions: attributeToArray(position),
      normals: normal ? attributeToArray(normal) : [],
      uv0: uv ? attributeToArray(uv) : [],
      colors: color ? attributeToArray(color, 4, 1) : [],
      indices: geometry.index ? Array.from(geometry.index.array, Number) : [],
      groups: geometry.groups.map((group) => ({
        start: group.start,
        count: group.count,
        materialIndex: Array.isArray(mesh.material) ? group.materialIndex ?? 0 : 0,
      })),
      materialIds: materialIdList,
      ...skinAttributes,
      morphTargets,
    });
    morphTargetNamesByMeshId.set(id, morphTargetNames);
    return id;
  };

  const registerLinePrimitive = async (line: Line): Promise<string | undefined> => {
    const lineMaterials = Array.isArray(line.material) ? line.material : [line.material];
    const unsupportedMaterial = lineMaterials.find((material) => material.type !== "LineBasicMaterial");
    if (unsupportedMaterial) {
      warnings.push(
        `${line.name || line.uuid}: ${unsupportedMaterial.type} is not supported for ${line.type}; the object is preserved as a transform only.`,
      );
      return undefined;
    }
    const type = linePrimitiveType(line);
    const key = `${type}|${line.geometry.uuid}|${lineMaterials.map((material) => material.uuid).join("|")}`;
    const existing = primitiveIds.get(key);
    if (existing) return existing;
    const position = requiredPrimitivePosition(line.geometry, line);
    const sourceIndices = primitiveSourceIndices(line.geometry, position.count, line);
    const { indices, groups } = canonicalPrimitiveGroups(type, line.geometry, sourceIndices, lineMaterials.length, line, warnings);
    const color = line.geometry.getAttribute("color") as BufferAttribute | undefined;
    if (hasMorphAttributes(line.geometry)) warnings.push(`${line.name || line.uuid}: Line morph targets are not exported; the base Line is preserved.`);
    const id = stablePrimitiveId(key);
    primitiveIds.set(key, id);
    primitives.push({
      id,
      name: line.geometry.name || line.name || id,
      type,
      positions: attributeToArray(position, 3, 0, `${line.name || line.uuid} position`),
      colors: color ? primitiveColorsToArray(color, line) : [],
      indices,
      groups,
      materialIds: await Promise.all(lineMaterials.map(registerMaterial)),
      spriteCenter: [0.5, 0.5],
    });
    return id;
  };

  const registerPointsPrimitive = async (points: Points): Promise<string | undefined> => {
    const pointsMaterials = Array.isArray(points.material) ? points.material : [points.material];
    const unsupportedMaterial = pointsMaterials.find((material) => material.type !== "PointsMaterial");
    if (unsupportedMaterial) {
      warnings.push(
        `${points.name || points.uuid}: ${unsupportedMaterial.type} is not supported for Points; the object is preserved as a transform only.`,
      );
      return undefined;
    }
    const key = `points|${points.geometry.uuid}|${pointsMaterials.map((material) => material.uuid).join("|")}`;
    const existing = primitiveIds.get(key);
    if (existing) return existing;
    const position = requiredPrimitivePosition(points.geometry, points);
    const sourceIndices = primitiveSourceIndices(points.geometry, position.count, points);
    const { indices, groups } = canonicalPrimitiveGroups("points", points.geometry, sourceIndices, pointsMaterials.length, points, warnings);
    const color = points.geometry.getAttribute("color") as BufferAttribute | undefined;
    if (hasMorphAttributes(points.geometry)) warnings.push(`${points.name || points.uuid}: Points morph targets are not exported; the base Points are preserved.`);
    for (const attributeName of ["size", "rotation"]) {
      if (points.geometry.getAttribute(attributeName)) {
        warnings.push(`${points.name || points.uuid}: per-point '${attributeName}' attributes are not exported.`);
      }
    }
    const id = stablePrimitiveId(key);
    primitiveIds.set(key, id);
    primitives.push({
      id,
      name: points.geometry.name || points.name || id,
      type: "points",
      positions: attributeToArray(position, 3, 0, `${points.name || points.uuid} position`),
      colors: color ? primitiveColorsToArray(color, points) : [],
      indices,
      groups,
      materialIds: await Promise.all(pointsMaterials.map(registerMaterial)),
      spriteCenter: [0.5, 0.5],
    });
    return id;
  };

  const registerSpritePrimitive = async (sprite: Sprite): Promise<string | undefined> => {
    if (sprite.material.type !== "SpriteMaterial") {
      warnings.push(`${sprite.name || sprite.uuid}: ${sprite.material.type} is not supported for Sprite; the object is preserved as a transform only.`);
      return undefined;
    }
    const center: [number, number] = [sprite.center.x, sprite.center.y];
    if (!center.every(Number.isFinite)) throw new Error(`Sprite '${sprite.name || sprite.uuid}' center must contain finite values.`);
    const key = `sprite|${sprite.material.uuid}|${center[0]},${center[1]}`;
    const existing = primitiveIds.get(key);
    if (existing) return existing;
    const id = stablePrimitiveId(key);
    primitiveIds.set(key, id);
    primitives.push({
      id,
      name: sprite.name || id,
      type: "sprite",
      positions: [],
      colors: [],
      indices: [],
      groups: [],
      materialIds: [await registerMaterial(sprite.material)],
      spriteCenter: center,
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
      primitiveId: "",
      instancedMeshId: "",
      skinId: "",
      morphWeights: [],
      metadataJson: safeJson(metadata),
      components,
    };
    const isInstancedMesh = Boolean((object as Mesh & { isInstancedMesh?: boolean }).isInstancedMesh);
    const fatLine = isUnsupportedFatLine(object);
    if ((object as Mesh).isMesh && !isInstancedMesh && !fatLine) {
      node.meshId = await registerMesh(object as Mesh);
      const morphTargetNames = morphTargetNamesByMeshId.get(node.meshId) ?? [];
      node.morphWeights = readMorphWeights(object as Mesh, morphTargetNames);
      if (morphTargetNames.length > 0) morphTargetNamesByObject.set(object, morphTargetNames);
      if ((object as SkinnedMesh).isSkinnedMesh) node.skinId = registerSkin(object as SkinnedMesh, id);
    }
    if (isInstancedMesh && instancedMeshMode === "gpu") {
      node.instancedMeshId = await exportGpuInstancedMesh(
        object as Mesh & InstancedMeshLike,
        registerMesh,
        instancedMeshes,
      );
      gpuInstancedMaterialObjects.add(object);
    }
    if (fatLine) {
      warnings.push(`${object.name || object.uuid}: ${object.type} fat-line renderables are preserved as transforms only.`);
    } else if (isLineLike(object)) {
      node.primitiveId = await registerLinePrimitive(object as Line) ?? "";
    } else if (isPoints(object)) {
      node.primitiveId = await registerPointsPrimitive(object as Points) ?? "";
    } else if (isSprite(object)) {
      node.primitiveId = await registerSpritePrimitive(object as Sprite) ?? "";
    }
    if ((node.meshId || node.primitiveId) && !isInstancedMesh) exportedMaterialObjects.add(object);
    if ((object as Camera).isCamera) node.camera = exportCamera(object as Camera);
    if ((object as Object3D & { isLight?: boolean }).isLight) node.light = exportLight(object as Object3D & LightLike);
    nodes.push(node);
    if (isInstancedMesh && instancedMeshMode === "expanded") {
      const instanceNodeIds = await exportExpandedInstances(
        object as Mesh & InstancedMeshLike,
        id,
        registerMesh,
        morphTargetNamesByMeshId,
        nodes,
        warnings,
      );
      exportedMaterialObjects.add(object);
      expandedMaterialTargetNodeIds.set(object, instanceNodeIds);
    }
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
  const materialSlots = collectExportedMaterialSlots(exportedMaterialObjects);
  const animationResult = exportAnimations(
    root,
    options.animations ?? root.animations,
    options,
    visitedObjects,
    morphTargetNamesByObject,
    materialSlots,
    gpuInstancedMaterialObjects,
    expandedMaterialTargetNodeIds,
    nodeIdFor,
    warnings,
  );
  return {
    format: THREE_UNITY_FORMAT,
    version: THREE_UNITY_VERSION,
    generator: "three-unity-bridge/0.1.0",
    name: options.name || root.name || "Three.js Scene",
    coordinateSystem: "threejs-right-handed-y-up",
    unitScaleMeters: options.unitScaleMeters ?? 1,
    nodes,
    meshes,
    instancedMeshes,
    primitives,
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

type ThreeUnityTransformAnimationProperty = "position" | "quaternion" | "scale";
type ThreeUnityMaterialAnimationProperty = Extract<
  ThreeUnityAnimationProperty,
  "materialBaseColor" | "materialEmissive" | "materialMetallic" | "materialRoughness" | "materialBaseMapST"
>;

interface ExportedMaterialSlot {
  object: Object3D;
  materialIndex: number;
  material: Material;
}

interface SampledMaterialState {
  materialBaseColor: [number, number, number, number];
  materialEmissive: [number, number, number];
  materialMetallic: [number];
  materialRoughness: [number];
  materialBaseMapST: [number, number, number, number];
}

type SampledMaterialChannels = Record<ThreeUnityMaterialAnimationProperty, number[]>;

const MATERIAL_ANIMATION_PROPERTIES: readonly ThreeUnityMaterialAnimationProperty[] = [
  "materialBaseColor",
  "materialEmissive",
  "materialMetallic",
  "materialRoughness",
  "materialBaseMapST",
];

interface SavedMorphWeights {
  reference: number[] | undefined;
  values: number[];
  initialTargetValues: number[];
}

function exportAnimations(
  root: Object3D,
  clips: AnimationClip[],
  options: ThreeUnityExportOptions,
  visitedObjects: Set<Object3D>,
  morphTargetNamesByObject: Map<Object3D, string[]>,
  materialSlots: ExportedMaterialSlot[],
  gpuInstancedMaterialObjects: Set<Object3D>,
  expandedMaterialTargetNodeIds: Map<Object3D, string[]>,
  nodeIdFor: (object: Object3D) => string,
  warnings: string[],
): { animations: ThreeUnityAnimation[]; defaultAnimationId: string; autoplayAnimation: boolean } {
  const sampleRate = options.animationSampleRate ?? 30;
  if (!Number.isFinite(sampleRate) || sampleRate <= 0) throw new Error(`animationSampleRate must be a positive finite number, received '${sampleRate}'.`);
  const exportedObjects = [...visitedObjects];
  const exportedObjectSet = new Set(exportedObjects);
  const exportedMaterialObjectSet = new Set(materialSlots.map((slot) => slot.object));
  const animations: ThreeUnityAnimation[] = [];
  const sourceByAnimationId = new Map<string, AnimationClip>();

  for (const [clipIndex, clip] of clips.entries()) {
    if (!Number.isFinite(clip.duration) || clip.duration <= 0) {
      warnings.push(`${clip.name || clip.uuid}: animation duration must be positive; the clip was not exported.`);
      continue;
    }
    if (clip.tracks.length === 0) warnings.push(`${clip.name || clip.uuid}: empty animation clip has no tracks.`);
    const properties = new Set<ThreeUnityTransformAnimationProperty>();
    let samplesMorphWeights = false;
    let samplesMaterials = false;
    const supportedTracks = clip.tracks.filter((track) => {
      let parsed: ReturnType<typeof PropertyBinding.parseTrackName>;
      try {
        parsed = PropertyBinding.parseTrackName(track.name);
      } catch (error) {
        if (isMultiMaterialUvTrackName(track.name)) {
          warnings.push(`${clip.name || clip.uuid}: track '${track.name}' targets multi-material base-map UV animation, which is not supported.`);
        } else {
          warnings.push(`${clip.name || clip.uuid}: track '${track.name}' could not be parsed (${error instanceof Error ? error.message : String(error)}).`);
        }
        return false;
      }
      const materialObject = parsed.objectName === "material" || parsed.objectName === "map";
      const transformProperty = !materialObject && isTransformAnimationProperty(parsed.propertyName) ? parsed.propertyName : undefined;
      const morphProperty = parsed.objectName === undefined && parsed.propertyName === "morphTargetInfluences";
      const materialProperty = materialObject && isSupportedMaterialSourceProperty(parsed.objectName, parsed.propertyName);
      if (!transformProperty && !morphProperty && !materialProperty) {
        warnings.push(`${clip.name || clip.uuid}: track '${track.name}' targets unsupported property '${parsed.propertyName}'.`);
        return false;
      }
      const target = PropertyBinding.findNode(root, parsed.nodeName);
      if (parsed.nodeName || morphProperty || materialProperty) {
        if (!target) {
          warnings.push(`${clip.name || clip.uuid}: track '${track.name}' does not resolve to a node under the exported root.`);
          return false;
        }
        if ((parsed.objectName === undefined || materialProperty) && !exportedObjectSet.has(target as Object3D)) {
          warnings.push(`${clip.name || clip.uuid}: track '${track.name}' resolves to a node that was not exported.`);
          return false;
        }
      }
      if (morphProperty) {
        if (!target || !morphTargetNamesByObject.has(target as Object3D)) {
          warnings.push(`${clip.name || clip.uuid}: track '${track.name}' does not resolve to an exported Mesh with position morph targets.`);
          return false;
        }
        samplesMorphWeights = true;
      } else if (transformProperty) {
        properties.add(transformProperty);
      } else if (materialProperty) {
        if (target && gpuInstancedMaterialObjects.has(target as Object3D)) {
          warnGpuInstancedMaterialTrack(target as Object3D, parsed, clip, track.name, warnings);
          return false;
        }
        if (!target || !validateMaterialSourceTrackTarget(target as Object3D, parsed, clip, track.name, exportedMaterialObjectSet, warnings)) return false;
        samplesMaterials = true;
      }
      return true;
    });
    const id = stableId("animation", clip.uuid, clipIndex);
    const tracks = bakeAnimationClip(
      root,
      clip,
      supportedTracks,
      properties,
      samplesMorphWeights,
      samplesMaterials,
      sampleRate,
      exportedObjects,
      morphTargetNamesByObject,
      materialSlots,
      expandedMaterialTargetNodeIds,
      nodeIdFor,
    );
    if (supportedTracks.length > 0 && tracks.length === 0) {
      warnings.push(`${clip.name || clip.uuid}: supported tracks produced no exported transform, morph-weight, or material changes.`);
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
  properties: Set<ThreeUnityTransformAnimationProperty>,
  samplesMorphWeights: boolean,
  samplesMaterials: boolean,
  sampleRate: number,
  objects: Object3D[],
  morphTargetNamesByObject: Map<Object3D, string[]>,
  materialSlots: ExportedMaterialSlot[],
  expandedMaterialTargetNodeIds: Map<Object3D, string[]>,
  nodeIdFor: (object: Object3D) => string,
): ThreeUnityAnimationTrack[] {
  if (sourceTracks.length === 0) return [];
  const saved = new Map<Object3D, SavedTransform>();
  const sampled = new Map<Object3D, SampledTransform>();
  const savedMorphWeights = new Map<Mesh, SavedMorphWeights>();
  const sampledMorphWeights = new Map<Mesh, number[][]>();
  const savedMaterialStates = new Map<ExportedMaterialSlot, SampledMaterialState>();
  const sampledMaterialChannels = new Map<ExportedMaterialSlot, SampledMaterialChannels>();
  for (const object of objects) {
    saved.set(object, {
      position: [object.position.x, object.position.y, object.position.z],
      quaternion: [object.quaternion.x, object.quaternion.y, object.quaternion.z, object.quaternion.w],
      scale: [object.scale.x, object.scale.y, object.scale.z],
    });
    sampled.set(object, { position: [], quaternion: [], scale: [] });
  }
  if (samplesMorphWeights) {
    for (const [object, targetNames] of morphTargetNamesByObject) {
      const mesh = object as Mesh;
      const reference = mesh.morphTargetInfluences;
      const values = reference ? [...reference] : [];
      const initialTargetValues = targetNames.map((targetName, targetIndex) => readMorphInfluence(mesh, targetIndex, targetName, clip, true));
      savedMorphWeights.set(mesh, { reference, values, initialTargetValues });
      sampledMorphWeights.set(mesh, targetNames.map(() => []));
    }
  }
  if (samplesMaterials) {
    for (const slot of materialSlots) {
      savedMaterialStates.set(slot, readSampledMaterialState(slot, clip, true));
      sampledMaterialChannels.set(slot, createSampledMaterialChannels());
    }
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
      if (samplesMorphWeights) {
        for (const [object, targetNames] of morphTargetNamesByObject) {
          const mesh = object as Mesh;
          const targetValues = sampledMorphWeights.get(mesh)!;
          for (let targetIndex = 0; targetIndex < targetNames.length; targetIndex += 1) {
            targetValues[targetIndex].push(readMorphInfluence(mesh, targetIndex, targetNames[targetIndex], clip, false));
          }
        }
      }
      if (samplesMaterials) {
        for (const slot of materialSlots) {
          const state = readSampledMaterialState(slot, clip, false);
          const channels = sampledMaterialChannels.get(slot)!;
          for (const property of MATERIAL_ANIMATION_PROPERTIES) channels[property].push(...state[property]);
        }
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
    for (const [mesh, morphWeights] of savedMorphWeights) {
      if (!morphWeights.reference) {
        mesh.morphTargetInfluences = undefined;
        continue;
      }
      mesh.morphTargetInfluences = morphWeights.reference;
      morphWeights.reference.length = morphWeights.values.length;
      for (let index = 0; index < morphWeights.values.length; index += 1) morphWeights.reference[index] = morphWeights.values[index];
    }
    for (const [slot, state] of savedMaterialStates) restoreSampledMaterialState(slot.material, state);
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
        morphTargetIndex: -1,
        materialIndex: -1,
        times: [...times],
        values: values[property],
        interpolation: "linear",
        baked: true,
      });
    }
  }
  for (const [object, targetNames] of morphTargetNamesByObject) {
    const mesh = object as Mesh;
    const initialValues = savedMorphWeights.get(mesh)?.initialTargetValues;
    const targetValues = sampledMorphWeights.get(mesh);
    if (!initialValues || !targetValues) continue;
    for (let targetIndex = 0; targetIndex < targetNames.length; targetIndex += 1) {
      if (!sampledValuesChange(targetValues[targetIndex], [initialValues[targetIndex]], 1)) continue;
      tracks.push({
        targetNodeId: nodeIdFor(object),
        property: "morphWeight",
        morphTargetIndex: targetIndex,
        materialIndex: -1,
        times: [...times],
        values: targetValues[targetIndex],
        interpolation: "linear",
        baked: true,
      });
    }
  }
  for (const slot of materialSlots) {
    const initialState = savedMaterialStates.get(slot);
    const sampledChannels = sampledMaterialChannels.get(slot);
    if (!initialState || !sampledChannels) continue;
    for (const property of MATERIAL_ANIMATION_PROPERTIES) {
      const dimensions = initialState[property].length;
      if (!sampledValuesChange(sampledChannels[property], initialState[property], dimensions)) continue;
      const targetNodeIds = expandedMaterialTargetNodeIds.get(slot.object) ?? [nodeIdFor(slot.object)];
      for (const targetNodeId of targetNodeIds) {
        tracks.push({
          targetNodeId,
          property,
          morphTargetIndex: -1,
          materialIndex: slot.materialIndex,
          times: [...times],
          values: sampledChannels[property],
          interpolation: "linear",
          baked: true,
        });
      }
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

function isTransformAnimationProperty(value: string): value is ThreeUnityTransformAnimationProperty {
  return value === "position" || value === "quaternion" || value === "scale";
}

function collectExportedMaterialSlots(objects: Set<Object3D>): ExportedMaterialSlot[] {
  const slots: ExportedMaterialSlot[] = [];
  for (const object of objects) {
    const renderable = object as MaterialRenderable;
    const materials = Array.isArray(renderable.material) ? renderable.material : [renderable.material];
    const groups = renderable.geometry?.groups ?? [];
    const materialIndices = Array.isArray(renderable.material)
      ? groups.length > 0
        ? [...new Set(groups.map((group) => group.materialIndex ?? 0))]
        : [0]
      : [0];
    for (const materialIndex of materialIndices) {
      if (!Number.isInteger(materialIndex) || materialIndex < 0 || materialIndex >= materials.length) {
        throw new Error(
          `Renderable '${object.name || object.uuid}' uses source material index '${materialIndex}' but has ${materials.length} material slot(s).`,
        );
      }
      slots.push({ object, materialIndex, material: materials[materialIndex] });
    }
  }
  return slots;
}

function isSupportedMaterialSourceProperty(objectName: string | undefined, propertyName: string): boolean {
  if (objectName === "map") return propertyName === "offset" || propertyName === "repeat";
  return objectName === "material"
    && (propertyName === "color"
      || propertyName === "opacity"
      || propertyName === "emissive"
      || propertyName === "metalness"
      || propertyName === "roughness");
}

function isMultiMaterialUvTrackName(trackName: string): boolean {
  return /\.material\[[^\]]+\]\.map\.(?:offset|repeat)(?:\[[^\]]+\])?$/.test(trackName);
}

function warnGpuInstancedMaterialTrack(
  target: Object3D,
  parsed: ReturnType<typeof PropertyBinding.parseTrackName>,
  clip: AnimationClip,
  trackName: string,
  warnings: string[],
): void {
  const materialIndex = parsed.objectIndex ?? 0;
  const property = `${parsed.objectName}.${parsed.propertyName}`;
  warnings.push(
    `Animation '${clip.name || clip.uuid}' track '${trackName}' targets GPU InstancedMesh node '${target.name || target.uuid}', material index ${materialIndex}, property '${property}'; native instanced material animation is not supported.`,
  );
}

function validateMaterialSourceTrackTarget(
  target: Object3D,
  parsed: ReturnType<typeof PropertyBinding.parseTrackName>,
  clip: AnimationClip,
  trackName: string,
  exportedMaterialObjects: Set<Object3D>,
  warnings: string[],
): boolean {
  const label = `${clip.name || clip.uuid}: track '${trackName}'`;
  if (!exportedMaterialObjects.has(target)) {
    warnings.push(`${label} must resolve to an exported Mesh or primitive.`);
    return false;
  }
  const materialValue = (target as MaterialRenderable).material;
  if (parsed.objectName === "map") {
    if (Array.isArray(materialValue)) {
      warnings.push(`${label} targets multi-material base-map UV animation, which is not supported.`);
      return false;
    }
    if (materialRenderMode(materialValue) === "line") {
      warnings.push(`${label} targets unsupported LineBasicMaterial map animation.`);
      return false;
    }
    const map = (materialValue as MeshStandardMaterial).map;
    if (!map) {
      warnings.push(`${label} requires a single material with a base color map.`);
      return false;
    }
    return true;
  }

  let material: Material;
  if (Array.isArray(materialValue)) {
    if (parsed.objectIndex === undefined) {
      warnings.push(`${label} must specify a source material index for a material array.`);
      return false;
    }
    const materialIndex = Number(parsed.objectIndex);
    if (!Number.isInteger(materialIndex) || materialIndex < 0 || materialIndex >= materialValue.length) {
      warnings.push(`${label} references source material index '${parsed.objectIndex}', but the renderable has ${materialValue.length} material(s).`);
      return false;
    }
    material = materialValue[materialIndex];
  } else {
    if (parsed.objectIndex !== undefined) {
      warnings.push(`${label} specifies source material index '${parsed.objectIndex}' for a single-material renderable.`);
      return false;
    }
    material = materialValue;
  }

  const standard = material as MeshStandardMaterial;
  const renderMode = materialRenderMode(material);
  if (renderMode !== "surface" && (parsed.propertyName === "emissive" || parsed.propertyName === "metalness" || parsed.propertyName === "roughness")) {
    warnings.push(`${label} targets unsupported ${material.type} property '${parsed.propertyName}'.`);
    return false;
  }
  const supported = parsed.propertyName === "color"
    ? Boolean((material as Material & { color?: Color }).color)
    : parsed.propertyName === "opacity"
      ? typeof material.opacity === "number"
      : parsed.propertyName === "emissive"
        ? Boolean(standard.emissive)
        : parsed.propertyName === "metalness"
          ? typeof standard.metalness === "number"
          : typeof standard.roughness === "number";
  if (!supported) {
    warnings.push(`${label} targets property '${parsed.propertyName}' that material '${material.name || material.uuid}' does not expose.`);
    return false;
  }
  return true;
}

function createSampledMaterialChannels(): SampledMaterialChannels {
  return {
    materialBaseColor: [],
    materialEmissive: [],
    materialMetallic: [],
    materialRoughness: [],
    materialBaseMapST: [],
  };
}

function readSampledMaterialState(slot: ExportedMaterialSlot, clip: AnimationClip, initial: boolean): SampledMaterialState {
  const material = slot.material as Material & {
    color?: Color;
    emissive?: Color;
    metalness?: number;
    roughness?: number;
    map?: Texture | null;
  };
  const read = (value: number, property: string): number => {
    if (Number.isFinite(value)) return value;
    const phase = initial ? "initial" : "sampled";
    throw new Error(
      `Animation '${clip.name || clip.uuid}' ${phase} material state for node '${slot.object.name || slot.object.uuid}', source material index ${slot.materialIndex}, material '${material.name || material.uuid}', property '${property}' must be finite, received '${value}'.`,
    );
  };
  const color = material.color;
  const emissive = material.emissive;
  const map = material.map;
  return {
    materialBaseColor: [
      read(color?.r ?? 1, "color.r"),
      read(color?.g ?? 1, "color.g"),
      read(color?.b ?? 1, "color.b"),
      read(material.opacity, "opacity"),
    ],
    materialEmissive: [
      read(emissive?.r ?? 0, "emissive.r"),
      read(emissive?.g ?? 0, "emissive.g"),
      read(emissive?.b ?? 0, "emissive.b"),
    ],
    materialMetallic: [read(material.metalness ?? 0, "metalness")],
    materialRoughness: [read(material.roughness ?? 0.5, "roughness")],
    materialBaseMapST: [
      read(map?.repeat.x ?? 1, "map.repeat.x"),
      read(map?.repeat.y ?? 1, "map.repeat.y"),
      read(map?.offset.x ?? 0, "map.offset.x"),
      read(map?.offset.y ?? 0, "map.offset.y"),
    ],
  };
}

function restoreSampledMaterialState(materialValue: Material, state: SampledMaterialState): void {
  const material = materialValue as Material & {
    color?: Color;
    emissive?: Color;
    metalness?: number;
    roughness?: number;
    map?: Texture | null;
  };
  material.color?.setRGB(state.materialBaseColor[0], state.materialBaseColor[1], state.materialBaseColor[2]);
  material.opacity = state.materialBaseColor[3];
  material.emissive?.setRGB(...state.materialEmissive);
  if (typeof material.metalness === "number") material.metalness = state.materialMetallic[0];
  if (typeof material.roughness === "number") material.roughness = state.materialRoughness[0];
  material.map?.repeat.set(state.materialBaseMapST[0], state.materialBaseMapST[1]);
  material.map?.offset.set(state.materialBaseMapST[2], state.materialBaseMapST[3]);
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

function isLineLike(object: Object3D): boolean {
  return Boolean((object as Object3D & { isLine?: boolean }).isLine);
}

function isPoints(object: Object3D): boolean {
  return Boolean((object as Object3D & { isPoints?: boolean }).isPoints);
}

function isSprite(object: Object3D): boolean {
  return Boolean((object as Object3D & { isSprite?: boolean }).isSprite);
}

function isUnsupportedFatLine(object: Object3D): boolean {
  const value = object as Object3D & { isLine2?: boolean; isLineSegments2?: boolean };
  return Boolean(value.isLine2 || value.isLineSegments2 || object.type === "Line2" || object.type === "LineSegments2");
}

function linePrimitiveType(line: Line): Extract<ThreeUnityPrimitiveType, "line" | "line-segments" | "line-loop"> {
  const value = line as Line & { isLineLoop?: boolean; isLineSegments?: boolean };
  if (value.isLineSegments) return "line-segments";
  if (value.isLineLoop) return "line-loop";
  return "line";
}

function materialRenderMode(material: Material): ThreeUnityMaterial["renderMode"] {
  if (material.type === "LineBasicMaterial") return "line";
  if (material.type === "PointsMaterial") return "points";
  if (material.type === "SpriteMaterial") return "sprite";
  return "surface";
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
  instanceMatrix: BufferAttribute;
  instanceColor?: BufferAttribute | null;
  morphTexture?: Texture | null;
  getMatrixAt(index: number, matrix: import("three").Matrix4): void;
  getColorAt(index: number, color: Color): void;
}

interface MaterialRenderable extends Object3D {
  material: Material | Material[];
  geometry?: BufferGeometry;
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

async function exportGpuInstancedMesh(
  object: Mesh & InstancedMeshLike,
  registerMesh: (mesh: Mesh) => Promise<string>,
  instancedMeshes: ThreeUnityInstancedMesh[],
): Promise<string> {
  const label = `InstancedMesh '${object.name || object.uuid}'`;
  if (!Number.isInteger(object.count) || object.count < 0) {
    throw new Error(`${label} count must be a non-negative integer, received '${object.count}'.`);
  }
  if (object.count > object.instanceMatrix.count) {
    throw new Error(`${label} count ${object.count} exceeds instanceMatrix count ${object.instanceMatrix.count}.`);
  }
  if (hasMorphAttributes(object.geometry) || object.morphTexture) {
    throw new Error(`${label} uses morph targets, which are not supported in GPU mode; use a normal Mesh or instancedMeshMode: 'expanded'.`);
  }
  validateGpuInstancedMeshGroups(object);

  const meshId = await registerMesh(object);
  const id = stableId("instanced_mesh", object.uuid, instancedMeshes.length);
  const matrices: number[] = [];
  const colors: number[] = [];
  const matrix = object.matrix.clone();
  const color = new Color();
  const exportsColors = Boolean(object.instanceColor);
  for (let index = 0; index < object.count; index += 1) {
    object.getMatrixAt(index, matrix);
    matrices.push(...finiteAffineMatrixElements(matrix.elements, `${label} instance ${index} matrix`));
    if (exportsColors) {
      object.getColorAt(index, color);
      const components = [color.r, color.g, color.b, 1];
      for (const [component, value] of components.entries()) {
        if (!Number.isFinite(value)) throw new Error(`${label} instance ${index} color component ${component} must be finite, received '${value}'.`);
      }
      colors.push(...components);
    }
  }
  instancedMeshes.push({
    id,
    name: `${object.name || "InstancedMesh"} Instances`,
    meshId,
    count: object.count,
    matrices,
    colors,
  });
  return id;
}

function validateGpuInstancedMeshGroups(object: Mesh & InstancedMeshLike): void {
  const label = `InstancedMesh '${object.name || object.uuid}'`;
  const materials = Array.isArray(object.material) ? object.material : [object.material];
  if (materials.length === 0) throw new Error(`${label} must have at least one material.`);
  const position = requiredAttribute(object.geometry, "position");
  const elementCount = object.geometry.index?.count ?? position.count;
  for (const [groupIndex, group] of object.geometry.groups.entries()) {
    const materialIndex = Array.isArray(object.material) ? group.materialIndex ?? 0 : 0;
    if (!Number.isInteger(materialIndex) || materialIndex < 0 || materialIndex >= materials.length) {
      throw new Error(`${label} group ${groupIndex} references material index '${materialIndex}' but has ${materials.length} material slot(s).`);
    }
    if (
      !Number.isInteger(group.start)
      || group.start < 0
      || !Number.isInteger(group.count)
      || group.count < 0
      || group.start + group.count > elementCount
    ) {
      throw new Error(`${label} group ${groupIndex} start/count '${group.start}/${group.count}' is outside the mesh element stream (${elementCount}).`);
    }
  }
}

async function exportExpandedInstances(
  object: Mesh & InstancedMeshLike,
  parentId: string,
  registerMesh: (mesh: Mesh) => Promise<string>,
  morphTargetNamesByMeshId: Map<string, string[]>,
  nodes: ThreeUnityNode[],
  warnings: string[],
): Promise<string[]> {
  const meshId = await registerMesh(object);
  const morphTargetCount = morphTargetNamesByMeshId.get(meshId)?.length ?? 0;
  const matrix = object.matrix.clone();
  const position = new Vector3();
  const rotation = new Quaternion();
  const scale = new Vector3();
  const nodeIds: string[] = [];
  for (let index = 0; index < object.count; index += 1) {
    object.getMatrixAt(index, matrix);
    matrix.decompose(position, rotation, scale);
    const id = `${stableId("node", object.uuid, nodes.length)}_instance_${index}`;
    nodeIds.push(id);
    nodes.push({
      id,
      name: `${object.name || "InstancedMesh"} ${index}`,
      parentId,
      visible: true,
      position: [position.x, position.y, position.z],
      quaternion: [rotation.x, rotation.y, rotation.z, rotation.w],
      scale: [scale.x, scale.y, scale.z],
      layersMask: object.layers.mask,
      meshId,
      primitiveId: "",
      instancedMeshId: "",
      skinId: "",
      morphWeights: Array.from({ length: morphTargetCount }, () => 0),
      metadataJson: `{\"threeUnityInstance\":${index}}`,
      components: [],
    });
  }
  if (object.instanceColor) {
    warnings.push(`${object.name || object.uuid}: per-instance colors are not exported when instancedMeshMode is 'expanded'.`);
  }
  return nodeIds;
}

function readMorphAttributes(mesh: Mesh, semantic: "position" | "normal"): BufferAttribute[] {
  const attributes = mesh.geometry.morphAttributes[semantic];
  if (attributes === undefined) return [];
  if (!Array.isArray(attributes)) throw new Error(`Mesh '${mesh.name || mesh.uuid}' morph ${semantic} attributes must be an array.`);
  return attributes as BufferAttribute[];
}

function createMorphTargetNames(mesh: Mesh, positionAttributes: BufferAttribute[], normalAttributes: BufferAttribute[]): string[] {
  if (positionAttributes.length === 0) {
    if (normalAttributes.length > 0) throw new Error(`Mesh '${mesh.name || mesh.uuid}' has normal morph targets but no position morph targets.`);
    return [];
  }
  if (normalAttributes.length > 0 && normalAttributes.length !== positionAttributes.length) {
    throw new Error(
      `Mesh '${mesh.name || mesh.uuid}' has ${positionAttributes.length} position morph targets but ${normalAttributes.length} normal morph targets.`,
    );
  }

  const dictionary = mesh.morphTargetDictionary ?? {};
  const usedNames = new Set<string>();
  return positionAttributes.map((attribute, targetIndex) => {
    const dictionaryName = Object.entries(dictionary)
      .filter(([name, index]) => name.length > 0 && index === targetIndex)
      .map(([name]) => name)
      .sort()[0];
    const normalAttributeName = normalAttributes[targetIndex]?.name;
    const baseName = dictionaryName || attribute.name || normalAttributeName || `MorphTarget_${targetIndex}`;
    let uniqueName = baseName;
    let suffix = 1;
    while (usedNames.has(uniqueName)) {
      uniqueName = `${baseName} [${suffix}]`;
      suffix += 1;
    }
    usedNames.add(uniqueName);
    return uniqueName;
  });
}

function exportMorphTargets(
  mesh: Mesh,
  basePosition: BufferAttribute,
  baseNormal: BufferAttribute | undefined,
  positionAttributes: BufferAttribute[],
  normalAttributes: BufferAttribute[],
  targetNames: string[],
): ThreeUnityMorphTarget[] {
  return positionAttributes.map((positionAttribute, targetIndex) => {
    const targetName = targetNames[targetIndex];
    const label = `Mesh '${mesh.name || mesh.uuid}' morph target '${targetName}'`;
    const normalAttribute = normalAttributes[targetIndex];
    if (normalAttribute && !baseNormal) throw new Error(`${label} has normal data but the base geometry has no normal attribute.`);
    return {
      name: targetName,
      positionDeltas: morphAttributeToDeltas(positionAttribute, basePosition, mesh.geometry.morphTargetsRelative, `${label} position`),
      normalDeltas: normalAttribute && baseNormal
        ? morphAttributeToDeltas(normalAttribute, baseNormal, mesh.geometry.morphTargetsRelative, `${label} normal`)
        : [],
    };
  });
}

function morphAttributeToDeltas(
  attribute: BufferAttribute,
  baseAttribute: BufferAttribute,
  relative: boolean,
  label: string,
): number[] {
  if (attribute.itemSize !== 3) throw new Error(`${label} attribute must have itemSize 3, received ${attribute.itemSize}.`);
  if (baseAttribute.itemSize < 3) throw new Error(`${label} base attribute must provide 3 components.`);
  if (attribute.count !== baseAttribute.count) {
    throw new Error(`${label} vertex count ${attribute.count} does not match the base vertex count ${baseAttribute.count}.`);
  }
  const deltas: number[] = [];
  for (let vertex = 0; vertex < attribute.count; vertex += 1) {
    for (let component = 0; component < 3; component += 1) {
      const targetValue = attribute.getComponent(vertex, component);
      const baseValue = relative ? 0 : baseAttribute.getComponent(vertex, component);
      const delta = targetValue - baseValue;
      if (!Number.isFinite(targetValue) || !Number.isFinite(baseValue) || !Number.isFinite(delta)) {
        throw new Error(`${label} contains a non-finite value at vertex ${vertex}, component ${component}.`);
      }
      deltas.push(delta);
    }
  }
  return deltas;
}

function readMorphWeights(mesh: Mesh, targetNames: string[]): number[] {
  return targetNames.map((targetName, targetIndex) => {
    const value = mesh.morphTargetInfluences?.[targetIndex] ?? 0;
    if (!Number.isFinite(value)) throw new Error(`Mesh '${mesh.name || mesh.uuid}' morph target '${targetName}' has non-finite influence '${value}'.`);
    return value;
  });
}

function readMorphInfluence(mesh: Mesh, targetIndex: number, targetName: string, clip: AnimationClip, initial: boolean): number {
  const value = mesh.morphTargetInfluences?.[targetIndex] ?? 0;
  if (!Number.isFinite(value)) {
    const phase = initial ? "initial" : "sampled";
    throw new Error(
      `Animation '${clip.name || clip.uuid}' ${phase} non-finite influence '${value}' for mesh '${mesh.name || mesh.uuid}' morph target '${targetName}' (${targetIndex}).`,
    );
  }
  return value;
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

function finiteAffineMatrixElements(elements: readonly number[], label: string): number[] {
  const values = finiteMatrixElements(elements, label);
  const tolerance = 1e-6;
  if (
    Math.abs(values[3]) > tolerance
    || Math.abs(values[7]) > tolerance
    || Math.abs(values[11]) > tolerance
    || Math.abs(values[15] - 1) > tolerance
  ) {
    throw new Error(`${label} must be an affine matrix.`);
  }
  return values;
}

function requiredAttribute(geometry: BufferGeometry, name: string): BufferAttribute {
  const value = geometry.getAttribute(name);
  if (!value) throw new Error(`Geometry '${geometry.name || geometry.uuid}' has no '${name}' attribute.`);
  return value as BufferAttribute;
}

function requiredPrimitivePosition(geometry: BufferGeometry, object: Object3D): BufferAttribute {
  const position = requiredAttribute(geometry, "position");
  if (position.itemSize !== 3) {
    throw new Error(`${object.type} '${object.name || object.uuid}' position attribute must have itemSize 3, received ${position.itemSize}.`);
  }
  if (position.count === 0) throw new Error(`${object.type} '${object.name || object.uuid}' position attribute must not be empty.`);
  return position;
}

function primitiveSourceIndices(geometry: BufferGeometry, vertexCount: number, object: Object3D): number[] {
  const source = geometry.index
    ? attributeToArray(geometry.index, 1, 0, `${object.name || object.uuid} index`)
    : Array.from({ length: vertexCount }, (_, index) => index);
  for (const [offset, index] of source.entries()) {
    if (!Number.isInteger(index) || index < 0 || index >= vertexCount) {
      throw new Error(`${object.type} '${object.name || object.uuid}' index ${offset} must be an integer in [0, ${vertexCount}), received '${index}'.`);
    }
  }
  return source;
}

function canonicalPrimitiveGroups(
  type: Exclude<ThreeUnityPrimitiveType, "sprite">,
  geometry: BufferGeometry,
  sourceIndices: number[],
  materialCount: number,
  object: Object3D,
  warnings: string[],
): Pick<ThreeUnityPrimitive, "indices" | "groups"> {
  const sourceGroups = geometry.groups.length > 0
    ? geometry.groups
    : [{ start: 0, count: sourceIndices.length, materialIndex: 0 }];
  const indices: number[] = [];
  const groups: ThreeUnityPrimitive["groups"] = [];
  for (const [groupIndex, group] of sourceGroups.entries()) {
    const materialIndex = group.materialIndex ?? 0;
    if (!Number.isInteger(materialIndex) || materialIndex < 0 || materialIndex >= materialCount) {
      throw new Error(
        `${object.type} '${object.name || object.uuid}' group ${groupIndex} references material index '${materialIndex}' but has ${materialCount} material slot(s).`,
      );
    }
    if (!Number.isInteger(group.start) || group.start < 0 || !Number.isInteger(group.count) || group.count < 0 || group.start + group.count > sourceIndices.length) {
      throw new Error(
        `${object.type} '${object.name || object.uuid}' group ${groupIndex} start/count '${group.start}/${group.count}' is outside the source index stream (${sourceIndices.length}).`,
      );
    }
    const sourceGroupIndices = sourceIndices.slice(group.start, group.start + group.count);
    const canonical = type === "points"
      ? sourceGroupIndices
      : canonicalLineIndices(type, sourceGroupIndices, object, groupIndex, warnings);
    groups.push({ start: indices.length, count: canonical.length, materialIndex });
    indices.push(...canonical);
  }
  return { indices, groups };
}

function canonicalLineIndices(
  type: Exclude<ThreeUnityPrimitiveType, "points" | "sprite">,
  source: number[],
  object: Object3D,
  groupIndex: number,
  warnings: string[],
): number[] {
  if (type === "line-segments") {
    if (source.length % 2 !== 0) {
      throw new Error(`${object.type} '${object.name || object.uuid}' group ${groupIndex} has odd index count ${source.length}.`);
    }
    return [...source];
  }
  if (type === "line-loop" && source.length < 2) {
    warnings.push(`${object.type} '${object.name || object.uuid}' group ${groupIndex} has fewer than 2 vertices and produces no segments.`);
    return [];
  }
  const canonical: number[] = [];
  for (let index = 1; index < source.length; index += 1) canonical.push(source[index - 1], source[index]);
  if (type === "line-loop" && source.length >= 2) canonical.push(source[source.length - 1], source[0]);
  return canonical;
}

function primitiveColorsToArray(attribute: BufferAttribute, object: Line | Points): number[] {
  if (attribute.itemSize !== 3 && attribute.itemSize !== 4) {
    throw new Error(`${object.type} '${object.name || object.uuid}' color attribute must have itemSize 3 or 4, received ${attribute.itemSize}.`);
  }
  const vertexCount = requiredAttribute(object.geometry, "position").count;
  if (attribute.count !== vertexCount) {
    throw new Error(`${object.type} '${object.name || object.uuid}' color count ${attribute.count} does not match position count ${vertexCount}.`);
  }
  return attributeToArray(attribute, 4, 1, `${object.name || object.uuid} color`);
}

function hasMorphAttributes(geometry: BufferGeometry): boolean {
  return Object.values(geometry.morphAttributes).some((attributes) => Array.isArray(attributes) && attributes.length > 0);
}

function attributeToArray(attribute: BufferAttribute, targetItemSize = attribute.itemSize, fill = 0, label?: string): number[] {
  const output: number[] = [];
  for (let index = 0; index < attribute.count; index += 1) {
    for (let component = 0; component < targetItemSize; component += 1) {
      const value = component < attribute.itemSize ? attribute.getComponent(index, component) : fill;
      if (label && !Number.isFinite(value)) throw new Error(`${label} contains a non-finite value at item ${index}, component ${component}.`);
      output.push(value);
    }
  }
  return output;
}

function exportTextureWrap(texture: Texture, axis: "wrapS" | "wrapT", value: number, warnings: string[]): ThreeUnityTextureWrap {
  if (value === RepeatWrapping) return "repeat";
  if (value === ClampToEdgeWrapping) return "clamp";
  if (value === MirroredRepeatWrapping) return "mirror";
  warnings.push(`Texture '${texture.name || texture.uuid}' has unsupported ${axis} value '${value}'; clamp was exported.`);
  return "clamp";
}

function exportTextureFilterMode(texture: Texture, warnings: string[]): ThreeUnityTextureFilterMode {
  const magFilter = texture.magFilter;
  const minFilter = texture.minFilter;
  const supportedMagFilter = magFilter === NearestFilter || magFilter === LinearFilter;
  const supportedMinFilter = minFilter === NearestFilter
    || minFilter === NearestMipmapNearestFilter
    || minFilter === NearestMipmapLinearFilter
    || minFilter === LinearFilter
    || minFilter === LinearMipmapNearestFilter
    || minFilter === LinearMipmapLinearFilter;
  if (!supportedMagFilter || !supportedMinFilter) {
    throw new Error(
      `Texture '${texture.name || texture.uuid}' has unsupported filter combination magFilter=${magFilter}, minFilter=${minFilter}.`,
    );
  }

  let filterMode: ThreeUnityTextureFilterMode;
  let exact: boolean;
  if (magFilter === NearestFilter && (minFilter === NearestFilter || minFilter === NearestMipmapNearestFilter)) {
    filterMode = "point";
    exact = true;
  } else if (magFilter === LinearFilter && minFilter === LinearMipmapLinearFilter) {
    filterMode = "trilinear";
    exact = true;
  } else {
    filterMode = "bilinear";
    exact = magFilter === LinearFilter && (minFilter === LinearFilter || minFilter === LinearMipmapNearestFilter);
  }
  if (!exact) {
    warnings.push(
      `Texture '${texture.name || texture.uuid}' filter combination magFilter=${magFilter}, minFilter=${minFilter} cannot be represented exactly by Unity; '${filterMode}' was exported.`,
    );
  }
  return filterMode;
}

function exportTextureColorSpace(texture: Texture): ThreeUnityTextureColorSpace {
  if (texture.colorSpace === SRGBColorSpace) return "srgb";
  if (texture.colorSpace === LinearSRGBColorSpace) return "linear";
  if (texture.colorSpace === NoColorSpace || texture.colorSpace === "") return "none";
  throw new Error(
    `Texture '${texture.name || texture.uuid}' has unsupported colorSpace '${String(texture.colorSpace)}'; convert it to sRGB, linear sRGB, or NoColorSpace before export.`,
  );
}

function readBaseColorTextureST(material: Material, texture: Texture | null, warnings: string[]): [number, number, number, number] {
  if (!texture) return [1, 1, 0, 0];
  const label = `Material '${material.name || material.uuid}' base color texture '${texture.name || texture.uuid}'`;
  const values: [number, number, number, number] = [texture.repeat.x, texture.repeat.y, texture.offset.x, texture.offset.y];
  for (const [index, value] of values.entries()) {
    if (!Number.isFinite(value)) throw new Error(`${label} ST component ${index} must be finite, received '${value}'.`);
  }
  if (texture.rotation !== 0) warnings.push(`${label} rotation '${texture.rotation}' is not converted.`);
  if (texture.center.x !== 0 || texture.center.y !== 0) {
    warnings.push(`${label} center '${texture.center.x}, ${texture.center.y}' is not converted.`);
  }
  if (!texture.matrixAutoUpdate && !isIdentityTextureMatrix(texture.matrix.elements)) {
    warnings.push(`${label} uses a custom UV matrix, which is not converted.`);
  }
  if (texture.channel !== 0) warnings.push(`${label} UV channel '${texture.channel}' is not converted.`);
  return values;
}

function isIdentityTextureMatrix(elements: readonly number[]): boolean {
  const identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
  return elements.length === identity.length && elements.every((value, index) => value === identity[index]);
}

function readColor(color: Color | undefined, fallback: [number, number, number]): [number, number, number] {
  return color ? [color.r, color.g, color.b] : fallback;
}

function stableId(prefix: string, uuid: string, fallback: number): string {
  return `${prefix}_${uuid ? uuid.replaceAll("-", "") : fallback}`;
}

function stablePrimitiveId(key: string): string {
  return `primitive_${encodeURIComponent(key).replaceAll("%", "_").replaceAll("-", "m").replaceAll(".", "p")}`;
}

function safeJson(value: unknown): string {
  try {
    return JSON.stringify(value ?? {});
  } catch {
    return "{}";
  }
}

type EncodedTextureFields = Pick<
  ThreeUnityTexture,
  "width" | "height" | "encoding" | "data" | "mimeType" | "pixelFormat" | "componentType"
>;

interface TextureImageLike {
  width?: number;
  height?: number;
  data?: unknown;
  currentSrc?: unknown;
  src?: unknown;
  toDataURL?: (type?: string) => string;
}

async function encodeTexture(
  texture: Texture,
  resolver: ThreeUnityTextureResolver | undefined,
): Promise<EncodedTextureFields> {
  const image = texture.image as TextureImageLike | undefined;
  assertSupportedTextureKind(texture, image);
  if ((texture as Texture & { isDataTexture?: boolean }).isDataTexture || image?.data !== undefined) {
    return encodeRawTexture(texture, image);
  }

  const width = imageDimensionHint(image?.width);
  const height = imageDimensionHint(image?.height);
  const explicitSource = discoverExplicitTextureSource(texture);
  if (explicitSource !== undefined) {
    return isDataUri(explicitSource)
      ? encodeDataImageUri(texture, explicitSource, width, height)
      : resolveEncodedTexture(texture, explicitSource, width, height, resolver, true);
  }

  let imageReadFailure = "";
  if (typeof image?.toDataURL === "function") {
    try {
      return encodeDataImageUri(texture, image.toDataURL("image/png"), width, height);
    } catch (error) {
      imageReadFailure = `image.toDataURL failed: ${errorMessage(error)}`;
    }
  }

  if (typeof document !== "undefined" && image && width > 0 && height > 0) {
    try {
      const canvas = document.createElement("canvas");
      canvas.width = width;
      canvas.height = height;
      const context = canvas.getContext("2d");
      if (!context) throw new Error("2D canvas context is unavailable");
      context.drawImage(image as unknown as CanvasImageSource, 0, 0);
      return encodeDataImageUri(texture, canvas.toDataURL("image/png"), width, height);
    } catch (error) {
      imageReadFailure = `browser canvas could not read the image (it may be cross-origin/tainted): ${errorMessage(error)}`;
    }
  }

  const automaticSource = discoverAutomaticTextureSource(texture, image);
  if (automaticSource !== undefined) {
    return isDataUri(automaticSource)
      ? encodeDataImageUri(texture, automaticSource, width, height)
      : resolveEncodedTexture(texture, automaticSource, width, height, resolver, false, imageReadFailure);
  }

  const suffix = imageReadFailure ? ` ${imageReadFailure}` : "";
  throw new Error(`Texture '${texture.name || texture.uuid}' could not be embedded.${suffix}`);
}

function encodeRawTexture(texture: Texture, image: TextureImageLike | undefined): EncodedTextureFields {
  const width = image?.width;
  const height = image?.height;
  if (!Number.isInteger(width) || (width ?? 0) <= 0 || !Number.isInteger(height) || (height ?? 0) <= 0) {
    throw unsupportedTextureError(texture, image, `raw dimensions must be positive integers, received ${String(width)}x${String(height)}`);
  }
  const pixelFormat = rawPixelFormat(texture, image);
  const componentType = rawComponentType(texture, image);
  const channels = pixelFormat === "r" ? 1 : pixelFormat === "rg" ? 2 : pixelFormat === "rgb" ? 3 : 4;
  const expectedElementCount = width! * height! * channels;
  if (!Number.isSafeInteger(expectedElementCount)) {
    throw unsupportedTextureError(texture, image, `raw dimensions and pixel format exceed the supported element count`);
  }
  const data = image?.data;
  let bytes: Uint8Array;
  if (componentType === "uint8") {
    if (!(data instanceof Uint8Array) && !(data instanceof Uint8ClampedArray)) {
      throw unsupportedTextureError(texture, image, "UnsignedByteType requires Uint8Array or Uint8ClampedArray image data");
    }
    if (data.length !== expectedElementCount) {
      throw unsupportedTextureError(texture, image, `expected ${expectedElementCount} elements for ${width}x${height} ${pixelFormat}, received ${data.length}`);
    }
    bytes = new Uint8Array(data.length);
    bytes.set(data);
  } else if (componentType === "float16") {
    if (!(data instanceof Uint16Array)) {
      throw unsupportedTextureError(texture, image, "HalfFloatType requires Uint16Array image data containing IEEE 754 half-float bits");
    }
    if (data.length !== expectedElementCount) {
      throw unsupportedTextureError(texture, image, `expected ${expectedElementCount} elements for ${width}x${height} ${pixelFormat}, received ${data.length}`);
    }
    bytes = new Uint8Array(data.length * 2);
    const view = new DataView(bytes.buffer);
    for (let index = 0; index < data.length; index += 1) view.setUint16(index * 2, data[index], true);
  } else {
    if (!(data instanceof Float32Array)) {
      throw unsupportedTextureError(texture, image, "FloatType requires Float32Array image data");
    }
    if (data.length !== expectedElementCount) {
      throw unsupportedTextureError(texture, image, `expected ${expectedElementCount} elements for ${width}x${height} ${pixelFormat}, received ${data.length}`);
    }
    bytes = new Uint8Array(data.length * 4);
    const view = new DataView(bytes.buffer);
    for (let index = 0; index < data.length; index += 1) {
      if (!Number.isFinite(data[index])) {
        throw unsupportedTextureError(texture, image, `Float32 image data contains a non-finite value at element ${index}`);
      }
      view.setFloat32(index * 4, data[index], true);
    }
  }
  return {
    width: width!,
    height: height!,
    encoding: "raw",
    data: bytesToBase64(bytes),
    mimeType: "",
    pixelFormat,
    componentType,
  };
}

function rawPixelFormat(texture: Texture, image: TextureImageLike | undefined): Exclude<ThreeUnityTexturePixelFormat, ""> {
  if (texture.format === RedFormat) return "r";
  if (texture.format === RGFormat) return "rg";
  if (texture.format === RGBFormat) return "rgb";
  if (texture.format === RGBAFormat) return "rgba";
  throw unsupportedTextureError(texture, image, "supported raw formats are RedFormat, RGFormat, RGBFormat, and RGBAFormat");
}

function rawComponentType(texture: Texture, image: TextureImageLike | undefined): Exclude<ThreeUnityTextureComponentType, ""> {
  if (texture.type === UnsignedByteType) return "uint8";
  if (texture.type === HalfFloatType) return "float16";
  if (texture.type === FloatType) return "float32";
  throw unsupportedTextureError(texture, image, "supported raw types are UnsignedByteType, HalfFloatType, and FloatType");
}

function assertSupportedTextureKind(texture: Texture, image: TextureImageLike | undefined): void {
  const flags = texture as Texture & {
    isCompressedTexture?: boolean;
    isCubeTexture?: boolean;
    isData3DTexture?: boolean;
    isDataArrayTexture?: boolean;
    isDepthTexture?: boolean;
    isFramebufferTexture?: boolean;
    isRenderTargetTexture?: boolean;
    isVideoTexture?: boolean;
  };
  const unsupportedKind = flags.isCompressedTexture ? "CompressedTexture"
    : flags.isData3DTexture ? "Data3DTexture"
      : flags.isDataArrayTexture ? "DataArrayTexture"
        : flags.isDepthTexture ? "DepthTexture"
          : flags.isVideoTexture ? "VideoTexture"
            : flags.isCubeTexture ? "CubeTexture"
              : flags.isRenderTargetTexture || flags.isFramebufferTexture ? "render-target-backed texture"
                : undefined;
  if (unsupportedKind) throw unsupportedTextureError(texture, image, `${unsupportedKind} is not supported`);
  if (texture.mipmaps.length > 0) throw unsupportedTextureError(texture, image, "custom mipmap chains are not supported");
}

function unsupportedTextureError(texture: Texture, image: TextureImageLike | undefined, reason: string): Error {
  const constructorName = image?.data === undefined
    ? "<none>"
    : (image.data as { constructor?: { name?: string } }).constructor?.name ?? typeof image.data;
  return new Error(
    `Texture '${texture.name || texture.uuid}' is not supported: ${reason}; Three.js format=${texture.format}, type=${texture.type}, image data constructor=${constructorName}.`,
  );
}

function discoverExplicitTextureSource(texture: Texture): string | undefined {
  const source = texture.userData.threeUnitySource;
  if (source === undefined) return undefined;
  if (typeof source !== "string" || source.trim().length === 0) {
    throw new Error(`Texture '${texture.name || texture.uuid}' userData.threeUnitySource must be a non-empty string.`);
  }
  return source;
}

function discoverAutomaticTextureSource(texture: Texture, image: TextureImageLike | undefined): string | undefined {
  const sourceData = texture.source?.data as TextureImageLike | undefined;
  for (const candidate of [sourceData?.currentSrc, sourceData?.src, image?.currentSrc, image?.src]) {
    if (typeof candidate === "string" && candidate.length > 0) return candidate;
  }
  return undefined;
}

function isDataUri(sourceUri: string): boolean {
  return sourceUri.toLowerCase().startsWith("data:");
}

function encodeDataImageUri(
  texture: Texture,
  sourceUri: string,
  width: number,
  height: number,
): EncodedTextureFields {
  const comma = sourceUri.indexOf(",");
  const metadata = comma >= 0 ? sourceUri.slice(5, comma).toLowerCase() : "";
  const mimeToken = metadata.split(";", 1)[0];
  const mimeType = normalizeImageMimeType(mimeToken);
  if (!mimeType) {
    throw new Error(`Texture '${texture.name || texture.uuid}' data URL has unsupported media type '${mimeToken || "<missing>"}'. Only image/png and image/jpeg are supported.`);
  }
  if (comma < 0 || metadata !== `${mimeToken};base64`) {
    throw new Error(`Texture '${texture.name || texture.uuid}' data URL for ${mimeToken} must use base64 encoding; percent-encoded image data is not supported.`);
  }
  const bytes = base64ToBytes(sourceUri.slice(comma + 1), `Texture '${texture.name || texture.uuid}' data URL`);
  assertEncodedImageBytes(bytes, mimeType, `Texture '${texture.name || texture.uuid}' data URL`);
  return encodedImageFields(bytes, mimeType, width, height);
}

async function resolveEncodedTexture(
  texture: Texture,
  sourceUri: string,
  width: number,
  height: number,
  resolver: ThreeUnityTextureResolver | undefined,
  explicit: boolean,
  imageReadFailure = "",
): Promise<EncodedTextureFields> {
  if (!resolver) {
    const failedRead = imageReadFailure ? ` ${imageReadFailure}` : "";
    throw new Error(
      `Texture '${texture.name || texture.uuid}' source '${sourceUri}' cannot be embedded; provide options.textureResolver.${failedRead}`,
    );
  }
  let resolved: ThreeUnityResolvedTextureSource | undefined;
  try {
    resolved = await resolver({ texture, sourceUri });
  } catch (error) {
    throw new Error(`Texture '${texture.name || texture.uuid}' resolver failed for source '${sourceUri}': ${errorMessage(error)}`);
  }
  if (!resolved) {
    const sourceKind = explicit ? "explicit source" : "source";
    throw new Error(`Texture '${texture.name || texture.uuid}' resolver did not handle ${sourceKind} '${sourceUri}'.`);
  }
  if (!(resolved.bytes instanceof Uint8Array)) {
    throw new Error(`Texture '${texture.name || texture.uuid}' resolver returned non-Uint8Array bytes for source '${sourceUri}'.`);
  }
  const mimeType = normalizeImageMimeType(resolved.mimeType);
  if (!mimeType) {
    throw new Error(`Texture '${texture.name || texture.uuid}' resolver returned unsupported MIME type '${String(resolved.mimeType)}' for source '${sourceUri}'.`);
  }
  assertEncodedImageBytes(resolved.bytes, mimeType, `Texture '${texture.name || texture.uuid}' source '${sourceUri}'`);
  return encodedImageFields(resolved.bytes, mimeType, width, height);
}

function encodedImageFields(
  bytes: Uint8Array,
  mimeType: Exclude<ThreeUnityTextureMimeType, "">,
  width: number,
  height: number,
): EncodedTextureFields {
  return {
    width,
    height,
    encoding: "encoded-image",
    data: bytesToBase64(bytes),
    mimeType,
    pixelFormat: "",
    componentType: "",
  };
}

function normalizeImageMimeType(value: unknown): Exclude<ThreeUnityTextureMimeType, ""> | undefined {
  if (typeof value !== "string") return undefined;
  const normalized = value.toLowerCase();
  if (normalized === "image/png") return "image/png";
  if (normalized === "image/jpeg" || normalized === "image/jpg") return "image/jpeg";
  return undefined;
}

function assertEncodedImageBytes(
  bytes: Uint8Array,
  mimeType: Exclude<ThreeUnityTextureMimeType, "">,
  context: string,
): void {
  const matches = mimeType === "image/png"
    ? bytes.length >= 8
      && bytes[0] === 0x89
      && bytes[1] === 0x50
      && bytes[2] === 0x4e
      && bytes[3] === 0x47
      && bytes[4] === 0x0d
      && bytes[5] === 0x0a
      && bytes[6] === 0x1a
      && bytes[7] === 0x0a
    : bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff;
  if (!matches) throw new Error(`${context} bytes do not match declared MIME type '${mimeType}'.`);
}

function base64ToBytes(base64: string, context: string): Uint8Array {
  if (base64.length === 0 || base64.length % 4 !== 0) throw new Error(`${context} contains invalid base64 image data.`);
  if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(base64)) {
    throw new Error(`${context} contains invalid base64 image data.`);
  }
  try {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
    return bytes;
  } catch (error) {
    throw new Error(`${context} contains invalid base64 image data: ${errorMessage(error)}`);
  }
}

function imageDimensionHint(value: number | undefined): number {
  return Number.isInteger(value) && value! >= 0 ? value! : 0;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function bytesToBase64(bytes: Uint8Array): string {
  if (typeof Buffer !== "undefined") return Buffer.from(bytes).toString("base64");
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

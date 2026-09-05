import {
  FloatType, HalfFloatType, RedFormat, RGFormat, RGBFormat, RGBAFormat, Texture, UnsignedByteType,
} from "three";
import type {
  ThreeUnityTexture, ThreeUnityTextureComponentType, ThreeUnityTextureMimeType, ThreeUnityTexturePixelFormat,
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

export async function encodeTexture(
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

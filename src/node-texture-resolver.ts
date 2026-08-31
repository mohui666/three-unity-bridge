import { readFile } from "node:fs/promises";
import { extname, isAbsolute, posix, resolve, win32 } from "node:path";
import { fileURLToPath } from "node:url";
import type { ThreeUnityResolvedTextureSource, ThreeUnityTextureResolver } from "./exporter.js";

type SupportedImageMimeType = ThreeUnityResolvedTextureSource["mimeType"];

export interface NodeTextureResolverOptions {
  baseDirectory?: string | URL;
}

export function createNodeTextureResolver(
  options: NodeTextureResolverOptions = {},
): ThreeUnityTextureResolver {
  return async ({ texture, sourceUri }) => {
    const textureName = texture.name || texture.uuid;
    try {
      if (/^https?:\/\//i.test(sourceUri)) {
        return await resolveHttpTexture(sourceUri);
      }

      const filePath = resolveFilePath(sourceUri, options.baseDirectory);
      if (filePath === undefined) return undefined;

      const bytes = new Uint8Array(await readFile(filePath));
      return validateImageBytes(bytes, filePath);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      throw new Error(`Texture '${textureName}' source '${sourceUri}': ${message}`);
    }
  };
}

async function resolveHttpTexture(sourceUri: string): Promise<ThreeUnityResolvedTextureSource> {
  const response = await fetch(sourceUri);
  if (!response.ok) {
    throw new Error(`HTTP request failed with status ${response.status} ${response.statusText}.`);
  }

  const bytes = new Uint8Array(await response.arrayBuffer());
  return validateImageBytes(bytes, new URL(sourceUri).pathname, response.headers.get("content-type"));
}

function resolveFilePath(sourceUri: string, baseDirectory: string | URL | undefined): string | undefined {
  if (/^file:\/\//i.test(sourceUri)) return fileURLToPath(new URL(sourceUri));
  if (isAbsolute(sourceUri) || win32.isAbsolute(sourceUri) || posix.isAbsolute(sourceUri)) return sourceUri;

  const scheme = /^([a-z][a-z\d+.-]*):/i.exec(sourceUri)?.[1];
  if (scheme !== undefined) return undefined;
  if (baseDirectory === undefined) {
    throw new Error("Relative file paths require options.baseDirectory; process.cwd() is not used implicitly.");
  }

  const basePath = baseDirectory instanceof URL ? fileURLToPath(baseDirectory) : baseDirectory;
  return resolve(basePath, sourceUri);
}

function validateImageBytes(
  bytes: Uint8Array,
  extensionSource: string,
  contentTypeHeader: string | null = null,
): ThreeUnityResolvedTextureSource {
  if (bytes.length === 0) throw new Error("Image source is empty.");

  const contentType = contentTypeHeader === null || contentTypeHeader.trim() === ""
    ? undefined
    : parseContentType(contentTypeHeader);
  const extensionType = mimeTypeFromExtension(extensionSource);
  const magicType = mimeTypeFromMagic(bytes);
  if (magicType === undefined) {
    throw new Error("Image bytes are neither PNG nor JPEG.");
  }

  if (contentType !== undefined && extensionType !== undefined && contentType !== extensionType) {
    throw new Error(`HTTP Content-Type '${contentType}' conflicts with file extension type '${extensionType}'.`);
  }
  if (contentType !== undefined && contentType !== magicType) {
    throw new Error(`HTTP Content-Type '${contentType}' conflicts with ${magicType} magic bytes.`);
  }
  if (extensionType !== undefined && extensionType !== magicType) {
    throw new Error(`File extension type '${extensionType}' conflicts with ${magicType} magic bytes.`);
  }

  return {
    bytes,
    mimeType: contentType ?? extensionType ?? magicType,
  };
}

function parseContentType(value: string): SupportedImageMimeType {
  const mediaType = value.split(";", 1)[0].trim().toLowerCase();
  if (mediaType === "image/png") return "image/png";
  if (mediaType === "image/jpeg" || mediaType === "image/jpg") return "image/jpeg";
  throw new Error(`Unsupported HTTP Content-Type '${mediaType}'; expected image/png or image/jpeg.`);
}

function mimeTypeFromExtension(path: string): SupportedImageMimeType | undefined {
  const extension = extname(path).toLowerCase();
  if (extension === ".png") return "image/png";
  if (extension === ".jpg" || extension === ".jpeg") return "image/jpeg";
  return undefined;
}

function mimeTypeFromMagic(bytes: Uint8Array): SupportedImageMimeType | undefined {
  const pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (bytes.length >= pngSignature.length && pngSignature.every((value, index) => bytes[index] === value)) {
    return "image/png";
  }
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) {
    return "image/jpeg";
  }
  return undefined;
}

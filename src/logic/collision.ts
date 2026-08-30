const BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

export interface Int3 {
  x: number;
  y: number;
  z: number;
}

export interface CollisionVolume {
  revision: number;
  origin: Int3;
  size: Int3;
  solid: readonly boolean[];
  fluid: readonly boolean[];
}

export interface EncodedCollisionVolume {
  revision: number;
  origin: Int3;
  size: Int3;
  solidBits: string;
  fluidBits: string;
}

export interface CollisionCellSample {
  solid: boolean;
  fluid: boolean;
}

export interface CollisionSamplingResult {
  volume: CollisionVolume;
  sampledCells: number;
  reusedCells: number;
}

export interface EncodedCollisionDelta {
  baseRevision: number;
  revision: number;
  origin: Int3;
  size: Int3;
  changeCount: number;
  changes: string;
}

export interface CollisionCellChange extends CollisionCellSample {
  index: number;
}

export type EncodedCollisionUpdate =
  | {
    type: "world.collision";
    encoding: "full";
    payload: EncodedCollisionVolume;
    characters: number;
    changedCells: number;
  }
  | {
    type: "world.collision.delta";
    encoding: "delta";
    payload: EncodedCollisionDelta;
    characters: number;
    changedCells: number;
  };

function validateSize(size: Int3): void {
  for (const [axis, value] of Object.entries(size)) {
    if (!Number.isInteger(value) || value <= 0) {
      throw new Error(`Collision volume size ${axis} must be a positive integer`);
    }
  }
}

function validateOrigin(origin: Int3): void {
  for (const [axis, value] of Object.entries(origin)) {
    if (!Number.isInteger(value)) {
      throw new Error(`Collision volume origin ${axis} must be an integer`);
    }
  }
}

function validateVolume(volume: CollisionVolume): number {
  if (!Number.isInteger(volume.revision) || volume.revision < 0) {
    throw new Error("Collision volume revision must be a non-negative integer");
  }
  validateOrigin(volume.origin);
  validateSize(volume.size);
  const cellCount = volume.size.x * volume.size.y * volume.size.z;
  if (!Number.isSafeInteger(cellCount)) {
    throw new Error("Collision volume cell count is too large");
  }
  if (volume.solid.length !== cellCount) {
    throw new Error(`Collision solid length ${volume.solid.length} does not match cell count ${cellCount}`);
  }
  if (volume.fluid.length !== cellCount) {
    throw new Error(`Collision fluid length ${volume.fluid.length} does not match cell count ${cellCount}`);
  }
  return cellCount;
}

function indexUnchecked(size: Int3, x: number, y: number, z: number): number {
  return x + size.x * (z + size.z * y);
}

function isInside(size: Int3, x: number, y: number, z: number): boolean {
  return x >= 0 && x < size.x && y >= 0 && y < size.y && z >= 0 && z < size.z;
}

function encodeBase64(bytes: Uint8Array): string {
  let encoded = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index];
    const hasSecond = index + 1 < bytes.length;
    const hasThird = index + 2 < bytes.length;
    const second = hasSecond ? bytes[index + 1] : 0;
    const third = hasThird ? bytes[index + 2] : 0;
    const value = (first << 16) | (second << 8) | third;

    encoded += BASE64_ALPHABET[(value >>> 18) & 63];
    encoded += BASE64_ALPHABET[(value >>> 12) & 63];
    encoded += hasSecond ? BASE64_ALPHABET[(value >>> 6) & 63] : "=";
    encoded += hasThird ? BASE64_ALPHABET[value & 63] : "=";
  }
  return encoded;
}

function decodeBase64(encoded: string): Uint8Array {
  if (encoded.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)) {
    throw new Error("Invalid base64 collision bitset");
  }

  const padding = encoded.endsWith("==") ? 2 : encoded.endsWith("=") ? 1 : 0;
  const bytes = new Uint8Array((encoded.length / 4) * 3 - padding);
  let output = 0;
  for (let index = 0; index < encoded.length; index += 4) {
    const a = BASE64_ALPHABET.indexOf(encoded[index]);
    const b = BASE64_ALPHABET.indexOf(encoded[index + 1]);
    const c = encoded[index + 2] === "=" ? 0 : BASE64_ALPHABET.indexOf(encoded[index + 2]);
    const d = encoded[index + 3] === "=" ? 0 : BASE64_ALPHABET.indexOf(encoded[index + 3]);
    if (a < 0 || b < 0 || c < 0 || d < 0) {
      throw new Error("Invalid base64 collision bitset");
    }
    const value = (a << 18) | (b << 12) | (c << 6) | d;
    if (output < bytes.length) bytes[output++] = (value >>> 16) & 255;
    if (output < bytes.length) bytes[output++] = (value >>> 8) & 255;
    if (output < bytes.length) bytes[output++] = value & 255;
  }
  return bytes;
}

function encodeCollisionBits(cells: readonly boolean[]): string {
  const bytes = new Uint8Array(Math.ceil(cells.length / 8));
  cells.forEach((occupied, index) => {
    if (occupied) bytes[index >>> 3] |= 1 << (index & 7);
  });
  return encodeBase64(bytes);
}

export function collisionIndex(size: Int3, x: number, y: number, z: number): number {
  validateSize(size);
  if (
    !Number.isInteger(x) || !Number.isInteger(y) || !Number.isInteger(z)
    || x < 0 || x >= size.x
    || y < 0 || y >= size.y
    || z < 0 || z >= size.z
  ) {
    throw new Error(`Collision coordinate (${x}, ${y}, ${z}) is outside the volume`);
  }
  return indexUnchecked(size, x, y, z);
}

export function decodeCollisionBits(encoded: string, cellCount: number): boolean[] {
  if (!Number.isInteger(cellCount) || cellCount < 0) {
    throw new Error("Collision cell count must be a non-negative integer");
  }
  const bytes = decodeBase64(encoded);
  const expectedBytes = Math.ceil(cellCount / 8);
  if (bytes.length !== expectedBytes) {
    throw new Error(`Collision bitset byte length ${bytes.length} does not match expected ${expectedBytes}`);
  }
  return Array.from({ length: cellCount }, (_, index) => (bytes[index >>> 3] & (1 << (index & 7))) !== 0);
}

export function encodeCollisionVolume(volume: CollisionVolume): EncodedCollisionVolume {
  validateVolume(volume);

  return {
    revision: volume.revision,
    origin: { ...volume.origin },
    size: { ...volume.size },
    solidBits: encodeCollisionBits(volume.solid),
    fluidBits: encodeCollisionBits(volume.fluid),
  };
}

export function sampleCollisionVolume(options: {
  revision: number;
  origin: Int3;
  size: Int3;
  sample(x: number, y: number, z: number): CollisionCellSample;
  previous?: CollisionVolume;
  invalidated?: readonly Int3[];
}): CollisionSamplingResult {
  if (typeof options.sample !== "function") {
    throw new TypeError("Collision sampler is required");
  }
  validateOrigin(options.origin);
  validateSize(options.size);
  if (!Number.isInteger(options.revision) || options.revision < 0) {
    throw new Error("Collision volume revision must be a non-negative integer");
  }
  if (options.previous) validateVolume(options.previous);

  const cellCount = options.size.x * options.size.y * options.size.z;
  const solid = new Array<boolean>(cellCount);
  const fluid = new Array<boolean>(cellCount);
  const invalidated = new Set(
    (options.invalidated ?? []).map(({ x, y, z }) => `${x},${y},${z}`),
  );
  let sampledCells = 0;
  let reusedCells = 0;

  for (let y = 0; y < options.size.y; y++) {
    for (let z = 0; z < options.size.z; z++) {
      for (let x = 0; x < options.size.x; x++) {
        const index = indexUnchecked(options.size, x, y, z);
        const worldX = options.origin.x + x;
        const worldY = options.origin.y + y;
        const worldZ = options.origin.z + z;
        const previousX = options.previous ? worldX - options.previous.origin.x : -1;
        const previousY = options.previous ? worldY - options.previous.origin.y : -1;
        const previousZ = options.previous ? worldZ - options.previous.origin.z : -1;
        if (
          options.previous
          && !invalidated.has(`${worldX},${worldY},${worldZ}`)
          && isInside(options.previous.size, previousX, previousY, previousZ)
        ) {
          const previousIndex = indexUnchecked(
            options.previous.size,
            previousX,
            previousY,
            previousZ,
          );
          solid[index] = options.previous.solid[previousIndex];
          fluid[index] = options.previous.fluid[previousIndex];
          reusedCells++;
          continue;
        }

        const cell = options.sample(worldX, worldY, worldZ);
        solid[index] = Boolean(cell?.solid);
        fluid[index] = Boolean(cell?.fluid);
        sampledCells++;
      }
    }
  }

  return {
    volume: {
      revision: options.revision,
      origin: { ...options.origin },
      size: { ...options.size },
      solid,
      fluid,
    },
    sampledCells,
    reusedCells,
  };
}

function encodeUnsignedVarint(value: number, output: number[]): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error("Collision delta token must be a non-negative safe integer");
  }
  let remaining = value;
  while (remaining >= 128) {
    output.push((remaining % 128) | 0x80);
    remaining = Math.floor(remaining / 128);
  }
  output.push(remaining);
}

export function decodeCollisionChanges(encoded: string, cellCount: number): CollisionCellChange[] {
  if (!Number.isSafeInteger(cellCount) || cellCount < 0) {
    throw new Error("Collision cell count must be a non-negative safe integer");
  }
  const bytes = decodeBase64(encoded);
  const changes: CollisionCellChange[] = [];
  let offset = 0;
  let previousIndex = -1;
  while (offset < bytes.length) {
    let packed = 0;
    let multiplier = 1;
    let complete = false;
    for (let byteIndex = 0; byteIndex < 8 && offset < bytes.length; byteIndex++) {
      const byte = bytes[offset++];
      packed += (byte & 0x7f) * multiplier;
      if ((byte & 0x80) === 0) {
        complete = true;
        break;
      }
      multiplier *= 128;
    }
    if (!complete || !Number.isSafeInteger(packed) || packed < 0) {
      throw new Error("Invalid packed collision delta varint");
    }
    const flags = packed % 4;
    const gap = Math.floor(packed / 4) + 1;
    const index = previousIndex + gap;
    if (index < 0 || index >= cellCount) {
      throw new Error(`Collision delta index ${index} is outside the volume`);
    }
    changes.push({
      index,
      solid: (flags & 0x01) !== 0,
      fluid: (flags & 0x02) !== 0,
    });
    previousIndex = index;
  }
  return changes;
}

export function encodeCollisionDelta(
  previous: CollisionVolume,
  next: CollisionVolume,
): EncodedCollisionDelta {
  validateVolume(previous);
  const cellCount = validateVolume(next);
  if (next.revision <= previous.revision) {
    throw new Error("Collision delta revision must be newer than its base revision");
  }

  const bytes: number[] = [];
  let previousChangedIndex = -1;
  let changeCount = 0;
  for (let y = 0; y < next.size.y; y++) {
    for (let z = 0; z < next.size.z; z++) {
      for (let x = 0; x < next.size.x; x++) {
        const nextIndex = indexUnchecked(next.size, x, y, z);
        const worldX = next.origin.x + x;
        const worldY = next.origin.y + y;
        const worldZ = next.origin.z + z;
        const previousX = worldX - previous.origin.x;
        const previousY = worldY - previous.origin.y;
        const previousZ = worldZ - previous.origin.z;
        let previousSolid = false;
        let previousFluid = false;
        if (isInside(previous.size, previousX, previousY, previousZ)) {
          const previousIndex = indexUnchecked(previous.size, previousX, previousY, previousZ);
          previousSolid = previous.solid[previousIndex];
          previousFluid = previous.fluid[previousIndex];
        }
        if (next.solid[nextIndex] === previousSolid && next.fluid[nextIndex] === previousFluid) {
          continue;
        }

        const gap = nextIndex - previousChangedIndex;
        const flags = (next.solid[nextIndex] ? 0x01 : 0) | (next.fluid[nextIndex] ? 0x02 : 0);
        const packed = (gap - 1) * 4 + flags;
        if (!Number.isSafeInteger(packed)) {
          throw new Error("Collision delta token is too large");
        }
        encodeUnsignedVarint(packed, bytes);
        previousChangedIndex = nextIndex;
        changeCount++;
      }
    }
  }

  if (cellCount === 0 && changeCount !== 0) {
    throw new Error("An empty collision volume cannot contain changes");
  }
  return {
    baseRevision: previous.revision,
    revision: next.revision,
    origin: { ...next.origin },
    size: { ...next.size },
    changeCount,
    changes: encodeBase64(Uint8Array.from(bytes)),
  };
}

export function encodeCollisionUpdate(
  previous: CollisionVolume | undefined,
  next: CollisionVolume,
  allowDelta = true,
): EncodedCollisionUpdate {
  const full = encodeCollisionVolume(next);
  const fullCharacters = JSON.stringify(full).length;
  if (!allowDelta || !previous) {
    return {
      type: "world.collision",
      encoding: "full",
      payload: full,
      characters: fullCharacters,
      changedCells: next.solid.length,
    };
  }

  const delta = encodeCollisionDelta(previous, next);
  const deltaCharacters = JSON.stringify(delta).length;
  if (deltaCharacters >= fullCharacters) {
    return {
      type: "world.collision",
      encoding: "full",
      payload: full,
      characters: fullCharacters,
      changedCells: next.solid.length,
    };
  }
  return {
    type: "world.collision.delta",
    encoding: "delta",
    payload: delta,
    characters: deltaCharacters,
    changedCells: delta.changeCount,
  };
}

import assert from "node:assert/strict";
import test from "node:test";
import {
  encodeLogicEnvelope,
  parseLogicEnvelope,
} from "../src/logic/protocol.js";
import {
  collisionIndex,
  decodeCollisionBits,
  decodeCollisionChanges,
  encodeCollisionDelta,
  encodeCollisionUpdate,
  encodeCollisionVolume,
  sampleCollisionVolume,
  type CollisionVolume,
} from "../src/logic/collision.js";

test("logic protocol rejects unsupported versions", () => {
  assert.throws(
    () => parseLogicEnvelope('{"protocol":2,"type":"bridge.hello","seq":1,"payload":{}}'),
    /Unsupported logic protocol 2/,
  );
});

test("logic protocol round-trips a valid envelope", () => {
  const encoded = encodeLogicEnvelope("player.input", 17, {
    dt: 1 / 60,
    moveX: -1,
    moveZ: 1,
  });

  assert.deepEqual(parseLogicEnvelope(encoded), {
    protocol: 1,
    type: "player.input",
    seq: 17,
    payload: { dt: 1 / 60, moveX: -1, moveZ: 1 },
  });
});

test("logic protocol optionally round-trips a session id without breaking legacy encoding", () => {
  const legacy = parseLogicEnvelope(encodeLogicEnvelope("bridge.hello", 0, {}));
  assert.equal(Object.prototype.hasOwnProperty.call(legacy, "sessionId"), false);

  const session = parseLogicEnvelope(
    encodeLogicEnvelope("bridge.hello", 0, {}, "session-a"),
  );
  assert.equal(session.sessionId, "session-a");
});

test("logic protocol rejects explicit empty or invalid session ids", () => {
  for (const sessionId of ["", "   ", " session-a", "session-a ", "x".repeat(129)]) {
    assert.throws(
      () => encodeLogicEnvelope("bridge.hello", 0, {}, sessionId),
      /non-empty trimmed string/,
    );
  }
  assert.throws(
    () => encodeLogicEnvelope("bridge.hello", 0, {}, undefined),
    /non-empty trimmed string/,
  );
  assert.throws(
    () => parseLogicEnvelope('{"protocol":1,"type":"bridge.hello","seq":0,"sessionId":null,"payload":{}}'),
    /non-empty trimmed string/,
  );
  assert.throws(
    () => parseLogicEnvelope('{"protocol":1,"type":"bridge.hello","seq":0,"sessionId":42,"payload":{}}'),
    /non-empty trimmed string/,
  );
});

test("logic protocol rejects malformed envelopes", () => {
  assert.throws(
    () => parseLogicEnvelope('{"protocol":1,"type":"","seq":0,"payload":{}}'),
    /non-empty type/,
  );
  assert.throws(
    () => parseLogicEnvelope('{"protocol":1,"type":"bridge.hello","seq":-1,"payload":{}}'),
    /non-negative integer seq/,
  );
  assert.throws(
    () => parseLogicEnvelope('{"protocol":1,"type":"bridge.hello","seq":0.5,"payload":{}}'),
    /non-negative integer seq/,
  );
  assert.throws(
    () => parseLogicEnvelope('{"protocol":1,"type":"bridge.hello","seq":0,"payload":null}'),
    /object payload/,
  );
  assert.throws(
    () => parseLogicEnvelope('{"protocol":1,"type":"bridge.hello","seq":0,"payload":[]}'),
    /object payload/,
  );
});

test("collision volumes use X-fastest, then Z, then Y bit order", () => {
  const volume = encodeCollisionVolume({
    revision: 4,
    origin: { x: -1, y: 2, z: 5 },
    size: { x: 2, y: 2, z: 2 },
    solid: [true, false, false, true, false, false, false, true],
    fluid: [false, true, false, false, false, false, true, false],
  });

  assert.equal(collisionIndex({ x: 2, y: 2, z: 2 }, 1, 1, 1), 7);
  assert.equal(volume.solidBits, "iQ==");
  assert.equal(volume.fluidBits, "Qg==");
  assert.deepEqual(decodeCollisionBits(volume.solidBits, 8), [
    true,
    false,
    false,
    true,
    false,
    false,
    false,
    true,
  ]);
});

test("collision volume rejects arrays with the wrong cell count", () => {
  assert.throws(
    () => encodeCollisionVolume({
      revision: 0,
      origin: { x: 0, y: 0, z: 0 },
      size: { x: 2, y: 2, z: 2 },
      solid: [true],
      fluid: new Array<boolean>(8).fill(false),
    }),
    /solid length 1 does not match cell count 8/,
  );
});

test("collision sampling reuses spatial overlap and only reads the entering slab", () => {
  const size = { x: 3, y: 2, z: 2 };
  const sample = (x: number, y: number, z: number) => ({
    solid: (x + y + z) % 2 === 0,
    fluid: y === 1 && z === 0,
  });
  const initial = sampleCollisionVolume({
    revision: 0,
    origin: { x: 0, y: 0, z: 0 },
    size,
    sample,
  });
  const shifted = sampleCollisionVolume({
    revision: 1,
    origin: { x: 1, y: 0, z: 0 },
    size,
    sample,
    previous: initial.volume,
  });

  assert.equal(initial.sampledCells, 12);
  assert.equal(initial.reusedCells, 0);
  assert.equal(shifted.sampledCells, 4);
  assert.equal(shifted.reusedCells, 8);
  assert.deepEqual(
    shifted.volume,
    sampleCollisionVolume({
      revision: 1,
      origin: { x: 1, y: 0, z: 0 },
      size,
      sample,
    }).volume,
  );
});

test("collision sampling refreshes explicitly invalidated overlap cells", () => {
  const previous = sampleCollisionVolume({
    revision: 4,
    origin: { x: 10, y: 20, z: 30 },
    size: { x: 2, y: 2, z: 2 },
    sample: () => ({ solid: false, fluid: false }),
  }).volume;
  let calls = 0;
  const sampled = sampleCollisionVolume({
    revision: 5,
    origin: { ...previous.origin },
    size: { ...previous.size },
    previous,
    invalidated: [{ x: 11, y: 20, z: 31 }],
    sample: () => {
      calls++;
      return { solid: true, fluid: false };
    },
  });

  assert.equal(calls, 1);
  assert.equal(sampled.sampledCells, 1);
  assert.equal(sampled.reusedCells, 7);
  assert.equal(sampled.volume.solid[collisionIndex(sampled.volume.size, 1, 0, 1)], true);
});

function applyCollisionDeltaForTest(
  previous: CollisionVolume,
  nextOrigin: { x: number; y: number; z: number },
  nextSize: { x: number; y: number; z: number },
  encodedChanges: string,
): Pick<CollisionVolume, "solid" | "fluid"> {
  const cellCount = nextSize.x * nextSize.y * nextSize.z;
  const solid = new Array<boolean>(cellCount).fill(false);
  const fluid = new Array<boolean>(cellCount).fill(false);
  for (let y = 0; y < nextSize.y; y++) {
    for (let z = 0; z < nextSize.z; z++) {
      for (let x = 0; x < nextSize.x; x++) {
        const worldX = nextOrigin.x + x;
        const worldY = nextOrigin.y + y;
        const worldZ = nextOrigin.z + z;
        const oldX = worldX - previous.origin.x;
        const oldY = worldY - previous.origin.y;
        const oldZ = worldZ - previous.origin.z;
        if (oldX < 0 || oldX >= previous.size.x
          || oldY < 0 || oldY >= previous.size.y
          || oldZ < 0 || oldZ >= previous.size.z) continue;
        const nextIndex = collisionIndex(nextSize, x, y, z);
        const oldIndex = collisionIndex(previous.size, oldX, oldY, oldZ);
        solid[nextIndex] = previous.solid[oldIndex];
        fluid[nextIndex] = previous.fluid[oldIndex];
      }
    }
  }
  for (const change of decodeCollisionChanges(encodedChanges, cellCount)) {
    solid[change.index] = change.solid;
    fluid[change.index] = change.fluid;
  }
  return { solid, fluid };
}

test("sparse collision deltas reproduce a spatially shifted window", () => {
  const size = { x: 11, y: 9, z: 11 };
  const sample = (x: number, y: number, z: number) => ({
    solid: y < 3 || (x === 6 && y === 4 && z === 5),
    fluid: y === 3 && z % 4 === 0,
  });
  const previous = sampleCollisionVolume({
    revision: 7,
    origin: { x: -5, y: -3, z: -5 },
    size,
    sample,
  }).volume;
  const next = sampleCollisionVolume({
    revision: 8,
    origin: { x: -4, y: -3, z: -5 },
    size,
    sample,
    previous,
  }).volume;
  const delta = encodeCollisionDelta(previous, next);
  const reconstructed = applyCollisionDeltaForTest(previous, next.origin, next.size, delta.changes);

  assert.equal(delta.baseRevision, 7);
  assert.equal(delta.revision, 8);
  assert.equal(decodeCollisionChanges(delta.changes, 1089).length, delta.changeCount);
  assert.deepEqual(reconstructed.solid, next.solid);
  assert.deepEqual(reconstructed.fluid, next.fluid);
});

test("collision update selects sparse deltas but retains full snapshots when smaller", () => {
  const size = { x: 11, y: 9, z: 11 };
  const previous: CollisionVolume = {
    revision: 0,
    origin: { x: 0, y: 0, z: 0 },
    size,
    solid: new Array<boolean>(1089).fill(false),
    fluid: new Array<boolean>(1089).fill(false),
  };
  const sparse: CollisionVolume = {
    ...previous,
    revision: 1,
    solid: previous.solid.slice(),
    fluid: previous.fluid.slice(),
  };
  (sparse.solid as boolean[])[500] = true;
  const sparseUpdate = encodeCollisionUpdate(previous, sparse);
  assert.equal(sparseUpdate.encoding, "delta");
  assert.equal(sparseUpdate.changedCells, 1);

  const dense: CollisionVolume = {
    ...previous,
    revision: 1,
    solid: new Array<boolean>(1089).fill(true),
    fluid: new Array<boolean>(1089).fill(true),
  };
  const denseUpdate = encodeCollisionUpdate(previous, dense);
  assert.equal(denseUpdate.encoding, "full");
});

test("collision delta decoding rejects truncated and out-of-range changes", () => {
  assert.throws(() => decodeCollisionChanges("gA==", 8), /packed collision delta varint/);
  assert.throws(() => decodeCollisionChanges("IA==", 8), /outside the volume/);
});

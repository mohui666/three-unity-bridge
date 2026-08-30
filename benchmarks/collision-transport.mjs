import {
  encodeCollisionUpdate,
  encodeCollisionVolume,
  sampleCollisionVolume,
} from "../dist/logic/collision.js";
import { encodeLogicEnvelope } from "../dist/logic/protocol.js";

const steps = 1_000;
const size = { x: 11, y: 9, z: 11 };
const sessionId = "s2x7m4k9q6v8n1";

function sampleWorld(x, y, z) {
  const hash = Math.abs((x * 73856093) ^ (z * 19349663));
  const height = 3 + (hash % 4);
  const solid = y <= height;
  const fluid = !solid && y <= 4 && hash % 13 < 3;
  return { solid, fluid };
}

let previous;
let baselineCharacters = 0;
let optimizedCharacters = 0;
let baselineSampledCells = 0;
let optimizedSampledCells = 0;
let reusedCells = 0;
let fullMessages = 0;
let deltaMessages = 0;
let deltaChangedCells = 0;

for (let revision = 0; revision < steps; revision++) {
  const origin = { x: revision - 5, y: 0, z: -5 };
  const baseline = sampleCollisionVolume({
    revision,
    origin,
    size,
    sample: sampleWorld,
  });
  baselineSampledCells += baseline.sampledCells;
  baselineCharacters += encodeLogicEnvelope(
    "world.collision",
    revision,
    encodeCollisionVolume(baseline.volume),
    sessionId,
  ).length;

  const optimized = sampleCollisionVolume({
    revision,
    origin,
    size,
    previous,
    sample: sampleWorld,
  });
  optimizedSampledCells += optimized.sampledCells;
  reusedCells += optimized.reusedCells;
  const update = encodeCollisionUpdate(previous, optimized.volume, true);
  optimizedCharacters += encodeLogicEnvelope(
    update.type,
    revision,
    update.payload,
    sessionId,
  ).length;
  if (update.encoding === "delta") {
    deltaMessages++;
    deltaChangedCells += update.changedCells;
  } else {
    fullMessages++;
  }
  previous = optimized.volume;
}

const samplingReduction = 1 - optimizedSampledCells / baselineSampledCells;
const characterReduction = 1 - optimizedCharacters / baselineCharacters;
const result = {
  steps,
  sessionIdCharacters: sessionId.length,
  windowCells: size.x * size.y * size.z,
  baselineSampledCells,
  optimizedSampledCells,
  reusedCells,
  samplingReductionPercent: Number((samplingReduction * 100).toFixed(1)),
  baselineEnvelopeCharacters: baselineCharacters,
  optimizedEnvelopeCharacters: optimizedCharacters,
  characterReductionPercent: Number((characterReduction * 100).toFixed(1)),
  fullMessages,
  deltaMessages,
  deltaChangedCells,
};

console.log(`COLLISION_TRANSPORT_BENCHMARK ${JSON.stringify(result)}`);
if (samplingReduction < 0.85) {
  console.error("Collision spatial reuse must reduce sampled cells by at least 85%.");
  process.exitCode = 1;
}
if (characterReduction < 0.35) {
  console.error("Collision delta transport must reduce envelope characters by at least 35%.");
  process.exitCode = 1;
}

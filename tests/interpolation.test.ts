import assert from "node:assert/strict";
import test from "node:test";
import { StateInterpolator } from "../src/logic/interpolation.js";

interface TestState {
  tick: number;
  x: number;
}

const lerp = (from: TestState, to: TestState, alpha: number): TestState => ({
  tick: to.tick,
  x: from.x + (to.x - from.x) * alpha,
});

test("state interpolation samples a delayed midpoint", () => {
  let now = 0;
  const buffer = new StateInterpolator<TestState>({
    interpolate: lerp,
    sequence: state => state.tick,
    delayMs: 20,
    now: () => now,
  });
  buffer.push({ tick: 1, x: 0 });
  now = 20;
  buffer.push({ tick: 2, x: 10 });
  now = 30;

  assert.deepEqual(buffer.sample(), { tick: 2, x: 5 });
  assert.equal(buffer.metrics.interpolatedFrames, 1);
});

test("state interpolation rejects stale sequences and never extrapolates", () => {
  const buffer = new StateInterpolator<TestState>({
    interpolate: lerp,
    sequence: state => state.tick,
    delayMs: 10,
  });
  assert.equal(buffer.push({ tick: 4, x: 2 }, 100), true);
  assert.equal(buffer.push({ tick: 3, x: 99 }, 110), false);
  assert.equal(buffer.push({ tick: 5, x: 6 }, 120), true);

  assert.deepEqual(buffer.sample(1_000), { tick: 5, x: 6 });
  assert.equal(buffer.metrics.staleStatesRejected, 1);
  assert.equal(buffer.metrics.heldFrames, 1);
});

test("zero delay returns the newest state and the buffer stays bounded", () => {
  const buffer = new StateInterpolator<TestState>({
    interpolate: lerp,
    delayMs: 0,
    maxBufferedStates: 3,
  });
  for (let tick = 0; tick < 10; tick++) buffer.push({ tick, x: tick }, tick * 10);

  assert.deepEqual(buffer.sample(100), { tick: 9, x: 9 });
  assert.equal(buffer.metrics.bufferedStates, 3);
});

test("state interpolation validates timing configuration", () => {
  assert.throws(
    () => new StateInterpolator<TestState>({ interpolate: lerp, delayMs: -1 }),
    /delay/,
  );
  assert.throws(
    () => new StateInterpolator<TestState>({ interpolate: lerp, maxBufferedStates: 1 }),
    /at least two/,
  );
});

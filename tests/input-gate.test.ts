import assert from "node:assert/strict";
import test from "node:test";
import { RealtimeInputGate } from "../src/logic/input-gate.js";

interface InputState {
  axis: number;
  jump: boolean;
  edge: boolean;
}

const equals = (left: InputState, right: InputState): boolean =>
  left.axis === right.axis && left.jump === right.jump && left.edge === right.edge;

test("input gate emits first sample, suppresses idle frames, and sends a heartbeat", () => {
  let now = 0;
  const gate = new RealtimeInputGate<InputState>({
    equals,
    now: () => now,
    minimumIntervalMs: 20,
    heartbeatMs: 100,
  });
  const idle = { axis: 0, jump: false, edge: false };

  assert.deepEqual(gate.offer(idle), idle);
  for (now = 10; now < 100; now += 10) assert.equal(gate.offer(idle), undefined);
  now = 100;
  assert.deepEqual(gate.offer(idle), idle);
  assert.deepEqual(gate.metrics, {
    sampled: 11,
    emitted: 2,
    changed: 1,
    heartbeats: 1,
    suppressed: 9,
    rateLimited: 0,
    pending: false,
  });
});

test("input gate coalesces analog samples to the newest bounded-rate value", () => {
  const gate = new RealtimeInputGate<InputState>({
    equals,
    minimumIntervalMs: 20,
    heartbeatMs: 100,
  });
  assert.equal(gate.offer({ axis: 0, jump: false, edge: false }, 0)?.axis, 0);
  assert.equal(gate.offer({ axis: 0.2, jump: false, edge: false }, 5), undefined);
  assert.equal(gate.offer({ axis: 0.6, jump: false, edge: false }, 10), undefined);
  assert.equal(gate.offer({ axis: 1, jump: false, edge: false }, 20)?.axis, 1);
  assert.equal(gate.metrics.rateLimited, 2);
  assert.equal(gate.metrics.pending, false);
});

test("urgent transitions bypass the rate limit and merge retains one-frame edges", () => {
  const gate = new RealtimeInputGate<InputState>({
    equals,
    merge: (pending, next) => ({ ...next, edge: pending.edge || next.edge }),
    isUrgent: (previous, next) => previous.jump !== next.jump || next.edge,
    minimumIntervalMs: 20,
    heartbeatMs: 100,
  });
  gate.offer({ axis: 0, jump: false, edge: false }, 0);

  assert.equal(gate.offer({ axis: 0.2, jump: false, edge: false }, 2), undefined);
  const edge = gate.offer({ axis: 0.4, jump: false, edge: true }, 3);
  assert.deepEqual(edge, { axis: 0.4, jump: false, edge: true });
  const jump = gate.offer({ axis: 0.5, jump: true, edge: false }, 4);
  assert.deepEqual(jump, { axis: 0.5, jump: true, edge: false });
  assert.equal(gate.metrics.changed, 3);
});

test("input gate validates timing and can reset stream state", () => {
  assert.throws(
    () => new RealtimeInputGate<InputState>({ equals, minimumIntervalMs: -1 }),
    /minimumIntervalMs/,
  );
  assert.throws(
    () => new RealtimeInputGate<InputState>({ equals, minimumIntervalMs: 20, heartbeatMs: 10 }),
    /heartbeatMs/,
  );

  const gate = new RealtimeInputGate<InputState>({ equals });
  const value = { axis: 0, jump: false, edge: false };
  gate.offer(value, 0);
  assert.equal(gate.offer(value, 1), undefined);
  gate.reset();
  assert.deepEqual(gate.offer(value, 2), value);
});

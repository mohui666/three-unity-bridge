import assert from "node:assert/strict";
import test from "node:test";

import { RuntimeLifecycleGate } from "../src/logic/lifecycle.js";

test("negotiated lifecycle state gates expensive frames and resumes without a catch-up burst", () => {
  const changes: boolean[] = [];
  const gate = new RuntimeLifecycleGate({ onChange: state => changes.push(state.active) });
  gate.configure(true);

  assert.deepEqual(gate.accept({ focused: false, paused: false, active: false, revision: 0 }), {
    focused: false,
    paused: false,
    active: false,
    revision: 0,
  });

  let rendered = 0;
  const render = (): void => { rendered++; };
  for (let frame = 0; frame < 10_000; frame++) {
    assert.equal(gate.run(render), false);
  }
  assert.equal(rendered, 0);

  assert.deepEqual(gate.accept({ focused: true, paused: false, active: true, revision: 1 }), {
    focused: true,
    paused: false,
    active: true,
    revision: 1,
  });
  for (let frame = 0; frame < 100; frame++) {
    assert.equal(gate.run(render), true);
  }

  assert.equal(rendered, 100);
  assert.deepEqual(changes, [false, true]);
  assert.deepEqual(gate.metrics, {
    accepted: 2,
    staleRejected: 0,
    invalidRejected: 0,
    unsupportedRejected: 0,
    callbackErrors: 0,
    activeFrames: 100,
    suspendedFrames: 10_000,
  });
  console.log("THREE_UNITY_RUNTIME_LIFECYCLE_BENCHMARK"
    + " totalFrames=10100 rendered=100 skipped=10000 resumeCatchup=0");
});

test("lifecycle gate rejects stale and inconsistent payloads without freezing browser authority", () => {
  const gate = new RuntimeLifecycleGate();
  gate.configure(true);

  assert.notEqual(gate.accept({ focused: false, paused: true, active: false, revision: 4 }), undefined);
  assert.equal(gate.active, false);
  assert.equal(gate.accept({ focused: true, paused: false, active: true, revision: 4 }), undefined);
  assert.equal(gate.accept({ focused: true, paused: false, active: false, revision: 5 }), undefined);
  assert.equal(gate.active, false);

  gate.configure(false);
  assert.equal(gate.active, true);
  assert.equal(gate.accept({ focused: false, paused: true, active: false, revision: 6 }), undefined);
  assert.equal(gate.active, true);
  assert.equal(gate.metrics.staleRejected, 1);
  assert.equal(gate.metrics.invalidRejected, 1);
  assert.equal(gate.metrics.unsupportedRejected, 1);
});

test("lifecycle callback failure restores active browser execution", () => {
  let work = 0;
  const gate = new RuntimeLifecycleGate({
    onChange: state => {
      if (!state.active) throw new Error("synthetic lifecycle callback failure");
    },
  });
  gate.configure(true);

  assert.equal(gate.accept({ focused: false, paused: false, active: false, revision: 0 }), undefined);
  assert.equal(gate.active, true);
  assert.equal(gate.run(() => { work++; }), true);
  assert.equal(work, 1);
  assert.equal(gate.metrics.callbackErrors, 1);
});

import assert from "node:assert/strict";
import test from "node:test";

import { RuntimeLifecycleGate } from "../src/logic/lifecycle.js";

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

import assert from "node:assert/strict";
import test from "node:test";

import {
  isRetryableReconnectReason,
  ReconnectBackoff,
} from "../src/logic/reconnect.js";

test("reconnect backoff defaults to 250 ms exponential attempts capped at 4 seconds", () => {
  let now = 0;
  const backoff = new ReconnectBackoff({ now: () => now });
  const dueAt: number[] = [];

  for (let attempt = 0; attempt < 5; attempt++) {
    assert.equal(backoff.schedule("state-timeout"), true);
    const nextAttemptAt = backoff.snapshot.nextAttemptAt;
    assert.notEqual(nextAttemptAt, undefined);
    dueAt.push(nextAttemptAt as number);
    now = (nextAttemptAt as number) - 1;
    assert.equal(backoff.poll(), undefined);
    now++;
    assert.equal(backoff.poll(), "state-timeout");
  }

  assert.deepEqual(dueAt, [250, 750, 1_750, 3_750, 7_750]);
  assert.equal(backoff.snapshot.exhausted, true);
  assert.equal(backoff.schedule("state-timeout"), false);
});

test("terminal compatibility and invalid-state reasons never schedule retries", () => {
  for (const reason of [
    "transport-unavailable",
    "profile-mismatch",
    "capability-not-advertised",
    "state-apply-error",
    "invalid-state",
    "invalid-player-state",
    "invalid-flight-state",
  ]) {
    assert.equal(isRetryableReconnectReason(reason), false, reason);
    const backoff = new ReconnectBackoff({ now: () => 0 });
    assert.equal(backoff.schedule(reason), false, reason);
    assert.equal(backoff.snapshot.scheduled, false, reason);
  }
  assert.equal(isRetryableReconnectReason("state-timeout"), true);
});

test("reconnect backoff validates configuration and clock values", () => {
  assert.throws(() => new ReconnectBackoff({ initialDelayMs: -1 }), /initialDelayMs/);
  assert.throws(
    () => new ReconnectBackoff({ initialDelayMs: 500, maxDelayMs: 250 }),
    /maxDelayMs/,
  );
  assert.throws(() => new ReconnectBackoff({ maxAttempts: 0 }), /maxAttempts/);
  assert.throws(() => new ReconnectBackoff({ now: () => Number.NaN }).schedule("timeout"), /finite/);
  assert.throws(() => new ReconnectBackoff().schedule(""), /non-empty/);
});

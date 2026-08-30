import assert from "node:assert/strict";
import test from "node:test";

import {
  createShopFlightAuthority,
  type ShopFlightAuthorityClient,
  type ShopFlightState,
} from "../src/logic/shop-flight.js";
import type { LogicEnvelope } from "../src/logic/protocol.js";

class FakeClient implements ShopFlightAuthorityClient {
  ready = false;
  sessionId = "session-0";
  readonly sent: Array<{ type: string; payload: object }> = [];
  readonly handlers = new Map<string, Set<(envelope: LogicEnvelope) => void>>();
  fallbackReason = "";

  on(type: string, handler: (envelope: LogicEnvelope) => void): () => void {
    let handlers = this.handlers.get(type);
    if (!handlers) {
      handlers = new Set();
      this.handlers.set(type, handlers);
    }
    handlers.add(handler);
    return () => handlers?.delete(handler);
  }

  start(gameId: string, capabilities: string[]): void {
    this.started = { gameId, capabilities };
  }

  restart(reason?: string): void {
    this.ready = false;
    this.sessionId = `session-${this.restarts.length + 1}`;
    this.restarts.push(reason ?? "manual");
  }

  readonly restarts: string[] = [];

  started?: { gameId: string; capabilities: string[] };
  send(type: string, payload: object): number {
    this.sent.push({ type, payload });
    return this.sent.length - 1;
  }
  pollWatchdog(): void { this.polls = (this.polls ?? 0) + 1; }
  polls?: number;
  activationAllowed = true;
  activationAttempts = 0;
  activateAuthority(): boolean {
    this.activationAttempts++;
    this.activated = true;
    return this.activationAllowed;
  }
  activated = false;
  fallback(reason: string): void {
    this.fallbackReason = reason;
    this.emit("bridge.fallback", { reason });
  }
  dispose(): void { this.disposed = true; }
  disposed = false;

  emit(type: string, payload: object, seq = 0): void {
    if (type === "bridge.ready") this.ready = true;
    if (type === "bridge.fallback") {
      this.ready = false;
      this.activated = false;
    }
    for (const handler of this.handlers.get(type) ?? []) {
      handler({ protocol: 1, type, seq, payload: payload as Record<string, unknown> });
    }
  }
}

function state(generation: number): ShopFlightState {
  return {
    generation,
    time: 1.2,
    amplitude: 0.7,
    flying: true,
    tick: 10,
    ackCommandSeq: 2,
    position: { x: 2, y: 9, z: -3 },
    rotation: { x: 0.01, y: 0, z: -0.02 },
  };
}

test("reusable shop authority stays on fallback until a matching state arrives", () => {
  const client = new FakeClient();
  const fallbackFrames: number[] = [];
  const applied: ShopFlightState[] = [];
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0.4, amplitude: 0.2, flying: true }),
    applyState: value => applied.push(value),
    runFallbackFrame: dt => fallbackFrames.push(dt),
  });

  assert.deepEqual(client.started, {
    gameId: "name-to-shop",
    capabilities: ["shop-flight-v1", "session-restart-v1"],
  });
  authority.update(0.1);
  client.emit("bridge.ready", { profile: "shop-flight-v1", fixedDeltaTime: 0.02 });
  assert.deepEqual(client.sent.at(-1), {
    type: "flight.bootstrap",
    payload: { generation: 0, time: 0.4, amplitude: 0.2, flying: true },
  });
  authority.update(0.2);
  client.emit("flight.state", state(0), 1);
  authority.update(0.3);

  assert.deepEqual(fallbackFrames, [0.1, 0.2]);
  assert.equal(applied.length, 1);
  assert.equal(authority.authorityActive, true);
  assert.equal(client.activated, true);
});

test("shop authority scopes commands and states to the current generation", () => {
  const client = new FakeClient();
  const applied: ShopFlightState[] = [];
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: value => applied.push(value),
    runFallbackFrame: () => undefined,
  });
  client.emit("bridge.ready", { profile: "shop-flight-v1", fixedDeltaTime: 0.02 });
  client.emit("flight.state", state(0), 1);

  assert.equal(authority.requestFlying(false), true);
  assert.deepEqual(client.sent.at(-1), {
    type: "flight.command",
    payload: { generation: 0, flying: false },
  });

  authority.reset();
  client.emit("flight.state", state(0), 2);
  assert.equal(applied.length, 1);
  client.emit("flight.state", state(1), 3);
  assert.equal(applied.length, 2);
});

test("shop authority rejects a mismatched Unity profile", () => {
  const client = new FakeClient();
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: () => undefined,
    runFallbackFrame: () => undefined,
  });

  client.emit("bridge.ready", { profile: "voxel-player-v1" });

  assert.equal(client.fallbackReason, "profile-mismatch");
  assert.equal(authority.authorityActive, false);
});

test("shop authority keeps JavaScript active through backoff and reboots from the current snapshot", () => {
  const client = new FakeClient();
  let now = 0;
  let snapshot = { time: 0.4, amplitude: 0.2, flying: true };
  const fallbackFrames: number[] = [];
  const applied: ShopFlightState[] = [];
  const authority = createShopFlightAuthority({
    client,
    reconnect: { now: () => now },
    getSnapshot: () => snapshot,
    applyState: value => applied.push(value),
    runFallbackFrame: dt => fallbackFrames.push(dt),
  });
  client.emit("bridge.ready", {
    profile: "shop-flight-v1",
    features: ["session-restart-v1"],
  });
  client.emit("flight.state", state(0), 1);
  assert.equal(authority.authorityActive, true);

  client.emit("bridge.fallback", { reason: "state-timeout" }, 2);
  assert.equal(authority.authorityActive, false);
  assert.equal(authority.generation, 1, "fallback must fence states from the old session");
  snapshot = { time: 8.5, amplitude: 0.45, flying: false };
  authority.update(0.1);
  now = 249;
  authority.update(0.2);
  assert.deepEqual(client.restarts, []);
  now = 250;
  authority.update(0.3);
  assert.deepEqual(client.restarts, ["state-timeout"]);
  assert.deepEqual(fallbackFrames, [0.1, 0.2, 0.3]);

  client.emit("bridge.ready", {
    profile: "shop-flight-v1",
    features: ["session-restart-v1"],
  }, 3);
  assert.deepEqual(client.sent.at(-1), {
    type: "flight.bootstrap",
    payload: { generation: 1, ...snapshot },
  });
  authority.update(0.4);
  assert.equal(authority.authorityActive, false, "ready alone must not stop JavaScript");
  client.emit("flight.state", state(0), 4);
  assert.equal(applied.length, 1, "a state from the fenced generation must be ignored");
  client.emit("flight.state", state(1), 5);
  authority.update(0.5);

  assert.equal(authority.authorityActive, true);
  assert.equal(applied.length, 2);
  assert.deepEqual(fallbackFrames, [0.1, 0.2, 0.3, 0.4]);
});

test("successful reconnect resets the retry ladder", () => {
  const client = new FakeClient();
  let now = 0;
  const authority = createShopFlightAuthority({
    client,
    reconnect: { now: () => now },
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: () => undefined,
    runFallbackFrame: () => undefined,
  });
  client.emit("bridge.ready", {
    profile: "shop-flight-v1",
    features: ["session-restart-v1"],
  });
  client.emit("flight.state", state(0), 1);
  client.emit("bridge.fallback", { reason: "state-timeout" }, 2);
  now = 250;
  authority.update(0.01);
  client.emit("bridge.ready", {
    profile: "shop-flight-v1",
    features: ["session-restart-v1"],
  }, 3);
  client.emit("flight.state", state(1), 4);

  client.emit("bridge.fallback", { reason: "state-timeout" }, 5);
  now = 499;
  authority.update(0.01);
  assert.equal(client.restarts.length, 1);
  now = 500;
  authority.update(0.01);
  assert.equal(client.restarts.length, 2, "a successful state must restore the 250 ms delay");
});

test("shop state is not applied when authority activation expires inside the handler", () => {
  const client = new FakeClient();
  const applied: ShopFlightState[] = [];
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: value => applied.push(value),
    runFallbackFrame: () => undefined,
  });
  client.emit("bridge.ready", { profile: "shop-flight-v1", fixedDeltaTime: 0.02 });
  client.activationAllowed = false;

  client.emit("flight.state", state(0), 1);

  assert.equal(client.activated, true);
  assert.equal(applied.length, 0);
  assert.equal(authority.authorityActive, false);
});

test("shop authority does not restart when Unity did not negotiate session restart", () => {
  const client = new FakeClient();
  let now = 0;
  const authority = createShopFlightAuthority({
    client,
    reconnect: { now: () => now },
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: () => undefined,
    runFallbackFrame: () => undefined,
  });
  client.emit("bridge.ready", { profile: "shop-flight-v1", features: [] });
  client.emit("flight.state", state(0), 1);
  client.emit("bridge.fallback", { reason: "state-timeout" }, 2);

  now = 5_000;
  authority.update(0.02);

  assert.deepEqual(client.restarts, []);
  assert.equal(authority.authorityActive, false);
});

test("shop authority rejects a higher envelope sequence with a repeated state tick", () => {
  const client = new FakeClient();
  const applied: ShopFlightState[] = [];
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: value => applied.push(value),
    runFallbackFrame: () => undefined,
  });
  client.emit("bridge.ready", { profile: "shop-flight-v1" });
  client.emit("flight.state", state(0), 1);
  client.emit("flight.state", { ...state(0), position: { x: 99, y: 9, z: -3 } }, 2);

  assert.equal(client.activationAttempts, 1);
  assert.equal(applied.length, 1);
  assert.equal(authority.authorityActive, true);
});

test("malformed current-generation flight state falls back terminally without retrying", () => {
  const client = new FakeClient();
  let now = 0;
  const applied: ShopFlightState[] = [];
  const authority = createShopFlightAuthority({
    client,
    reconnect: { now: () => now },
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: value => applied.push(value),
    runFallbackFrame: () => undefined,
  });
  client.emit("bridge.ready", {
    profile: "shop-flight-v1",
    features: ["session-restart-v1"],
  });

  client.emit("flight.state", { ...state(0), position: { x: Number.NaN, y: 0, z: 0 } }, 1);
  now = 5_000;
  authority.update(0.02);

  assert.equal(client.fallbackReason, "invalid-flight-state");
  assert.equal(applied.length, 0);
  assert.deepEqual(client.restarts, []);
  assert.equal(authority.authorityActive, false);
});

test("non-string fallback reasons are normalized without throwing", () => {
  const client = new FakeClient();
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: () => undefined,
    runFallbackFrame: () => undefined,
  });

  assert.doesNotThrow(() => client.emit("bridge.fallback", { reason: { bad: true } }, 1));
  assert.equal(authority.authorityActive, false);
});

test("state application errors enter terminal fallback and keep JavaScript authoritative", () => {
  let now = 0;
  let fallbackFrames = 0;
  const client = new FakeClient();
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: () => { throw new Error("scene mutation failed"); },
    runFallbackFrame: () => { fallbackFrames++; },
    reconnect: { now: () => now },
  });

  client.emit("bridge.ready", {
    profile: "shop-flight-v1",
    features: ["session-restart-v1"],
  });
  assert.doesNotThrow(() => client.emit("flight.state", state(0), 1));

  assert.equal(client.fallbackReason, "state-apply-error");
  assert.equal(client.activated, false);
  assert.equal(authority.authorityActive, false);
  assert.equal(authority.generation, 1);

  now = 10_000;
  authority.update(0.02);
  assert.equal(fallbackFrames, 1);
  assert.deepEqual(client.restarts, []);
});

test("authority-change activation errors use the same terminal fallback path", () => {
  const client = new FakeClient();
  const changes: Array<{ active: boolean; reason: string }> = [];
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: () => undefined,
    runFallbackFrame: () => undefined,
    onAuthorityChange: (active, reason) => {
      changes.push({ active, reason });
      if (active) throw new Error("activation observer failed");
    },
    reconnect: { now: () => Number.NaN },
  });

  client.emit("bridge.ready", {
    profile: "shop-flight-v1",
    features: ["session-restart-v1"],
  });
  assert.doesNotThrow(() => client.emit("flight.state", state(0), 1));

  assert.equal(client.fallbackReason, "state-apply-error");
  assert.equal(client.activated, false);
  assert.equal(authority.authorityActive, false);
  assert.equal(authority.generation, 1);
  assert.deepEqual(changes, [
    { active: true, reason: "state" },
    { active: false, reason: "state-apply-error" },
  ]);
});

test("shop authority leaves an injected client alive when disposed", () => {
  const client = new FakeClient();
  const authority = createShopFlightAuthority({
    client,
    getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
    applyState: () => undefined,
    runFallbackFrame: () => undefined,
  });

  authority.dispose();

  assert.equal(client.disposed, false);
  assert.equal(
    [...client.handlers.values()].every(handlers => handlers.size === 0),
    true,
  );
});

test("shop authority disposes its internally created client when the dispose callback throws", () => {
  const scope = globalThis as unknown as { chrome?: unknown };
  const previousChrome = scope.chrome;
  let messageHandler: ((event: { data: unknown }) => void) | undefined;
  const posted: unknown[] = [];
  let addCount = 0;
  let removeCount = 0;
  const disposeError = new Error("dispose observer failed");

  scope.chrome = {
    webview: {
      postMessage: (message: unknown) => { posted.push(message); },
      addEventListener: (
        type: "message",
        handler: (event: { data: unknown }) => void,
      ): void => {
        assert.equal(type, "message");
        addCount++;
        messageHandler = handler;
      },
      removeEventListener: (
        type: "message",
        handler: (event: { data: unknown }) => void,
      ): void => {
        assert.equal(type, "message");
        assert.equal(handler, messageHandler);
        removeCount++;
      },
    },
  };

  try {
    const authority = createShopFlightAuthority({
      getSnapshot: () => ({ time: 0, amplitude: 0, flying: false }),
      applyState: () => undefined,
      runFallbackFrame: () => undefined,
      onAuthorityChange: (active, reason) => {
        if (!active && reason === "dispose") throw disposeError;
      },
    });

    assert.equal(addCount, 1);
    const hello = posted.find(message =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "bridge.hello") as { sessionId?: string } | undefined;
    assert.equal(typeof hello?.sessionId, "string");
    assert.notEqual(messageHandler, undefined);
    messageHandler?.({
      data: {
        protocol: 1,
        sessionId: hello?.sessionId,
        type: "bridge.ready",
        seq: 0,
        payload: { profile: "shop-flight-v1", features: ["session-restart-v1"] },
      },
    });
    messageHandler?.({
      data: {
        protocol: 1,
        sessionId: hello?.sessionId,
        type: "flight.state",
        seq: 0,
        payload: state(0),
      },
    });
    assert.equal(authority.authorityActive, true);

    assert.throws(() => authority.dispose(), error => error === disposeError);
    assert.equal(removeCount, 1);
    assert.equal(authority.authorityActive, false);

    assert.doesNotThrow(() => authority.dispose());
    assert.equal(removeCount, 1);
  } finally {
    if (previousChrome === undefined) delete scope.chrome;
    else scope.chrome = previousChrome;
  }
});

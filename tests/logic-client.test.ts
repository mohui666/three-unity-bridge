import assert from "node:assert/strict";
import test from "node:test";
import {
  LogicClient,
  type LogicTransport,
  WebViewLogicTransport,
} from "../src/logic/client.js";
import { encodeLogicEnvelope } from "../src/logic/protocol.js";

class MemoryTransport implements LogicTransport {
  readonly sent: string[] = [];
  private readonly handlers = new Set<(message: string) => void>();

  constructor(readonly available = true) {}

  send(message: string): void {
    this.sent.push(message);
  }

  subscribe(handler: (message: string) => void): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  receive(message: string): void {
    for (const handler of this.handlers) handler(message);
  }
}

function messageAt(transport: MemoryTransport, index: number): Record<string, unknown> {
  return JSON.parse(transport.sent[index]) as Record<string, unknown>;
}

function sessionIds(...values: string[]): () => string {
  let index = 0;
  return () => values[index++] ?? `session-${index}`;
}

function receiveFor(
  transport: MemoryTransport,
  sessionId: string,
  type: string,
  seq: number,
  payload: object,
): void {
  transport.receive(encodeLogicEnvelope(type, seq, payload, sessionId));
}

test("unavailable transports keep JavaScript authority without throwing", () => {
  const transport = new MemoryTransport(false);
  const client = new LogicClient(transport, () => 0, sessionIds("session-a"));
  let fallbackReason = "";
  client.on("bridge.fallback", (envelope) => {
    fallbackReason = String(envelope.payload.reason);
  });

  client.start("little-cubes", ["voxel-player-v1"]);

  assert.equal(client.ready, false);
  assert.equal(client.authorityActive, false);
  assert.equal(client.phase, "fallback");
  assert.equal(client.sessionId, "session-a");
  assert.equal(fallbackReason, "transport-unavailable");
  assert.deepEqual(transport.sent, []);
});

test("start emits one versioned bridge hello", () => {
  const transport = new MemoryTransport();
  const client = new LogicClient(transport, () => 100, sessionIds("session-a"));

  client.start("little-cubes", ["voxel-player-v1"]);

  assert.deepEqual(messageAt(transport, 0), {
    protocol: 1,
    type: "bridge.hello",
    seq: 0,
    sessionId: "session-a",
    payload: { gameId: "little-cubes", capabilities: ["voxel-player-v1"] },
  });
  assert.equal(client.phase, "connecting");
});

test("default session ids are compact, non-empty, and unique", () => {
  const first = new LogicClient(new MemoryTransport(), () => 0);
  const second = new LogicClient(new MemoryTransport(), () => 0);
  first.start("game-a", []);
  second.start("game-b", []);

  assert.match(first.sessionId ?? "", /^[A-Za-z0-9_-]{13,16}$/);
  assert.match(second.sessionId ?? "", /^[A-Za-z0-9_-]{13,16}$/);
  assert.notEqual(first.sessionId, second.sessionId);
});

test("incoming messages reject stale sequence numbers per type", () => {
  const transport = new MemoryTransport();
  const client = new LogicClient(transport, () => 0, sessionIds("session-a"));
  const received: number[] = [];
  client.on("player.state", (envelope) => received.push(Number(envelope.payload.tick)));
  client.start("little-cubes", ["voxel-player-v1"]);

  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 4, { tick: 4 });
  receiveFor(transport, "session-a", "player.state", 3, { tick: 3 });
  receiveFor(transport, "session-a", "flight.state", 1, { tick: 20 });
  receiveFor(transport, "session-a", "player.state", 5, { tick: 5 });

  assert.deepEqual(received, [4, 5]);
});

test("latest-value streams send only the newest pending payload", () => {
  const transport = new MemoryTransport();
  const client = new LogicClient(transport, () => 0);

  client.sendLatest("player-input", "player.input", { moveX: 1 });
  client.sendLatest("player-input", "player.input", { moveX: -1 });
  client.flushLatest();

  assert.equal(transport.sent.length, 1);
  assert.equal((messageAt(transport, 0).payload as { moveX: number }).moveX, -1);
  assert.equal(client.metrics.latestValuesCoalesced, 1);
  assert.equal(client.metrics.pendingLatestStreams, 0);
  assert.equal(client.metrics.outboundMessages, 1);
  assert.equal(client.metrics.outboundCharacters, transport.sent[0].length);
});

test("client telemetry counts stale input and unattributed protocol errors", () => {
  const transport = new MemoryTransport();
  let now = 0;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  client.start("metrics-game", ["shop-flight-v1"]);
  const ready = encodeLogicEnvelope(
    "bridge.ready",
    0,
    { profile: "shop-flight-v1" },
    "session-a",
  );
  transport.receive(ready);
  const state = encodeLogicEnvelope("flight.state", 4, { tick: 4 }, "session-a");
  transport.receive(state);
  transport.receive(state);

  assert.deepEqual(client.metrics, {
    outboundMessages: 1,
    outboundCharacters: transport.sent[0].length,
    inboundMessages: 3,
    inboundCharacters: ready.length + state.length * 2,
    latestValuesCoalesced: 0,
    staleInboundRejected: 1,
    foreignSessionRejected: 0,
    terminalInboundRejected: 0,
    phaseInboundRejected: 0,
    protocolErrors: 0,
    fallbacks: 0,
    restarts: 0,
    pendingLatestStreams: 0,
  });

  transport.receive("not-json");
  assert.equal(client.metrics.protocolErrors, 1);
  assert.equal(client.metrics.fallbacks, 0);
  assert.equal(client.metrics.outboundMessages, 1);
  assert.equal(client.phase, "ready");

  now = 2_000;
  client.pollWatchdog();
  assert.equal(client.phase, "fallback");
  assert.equal(client.metrics.fallbacks, 1);
  assert.equal(client.metrics.outboundMessages, 2, "the attributed watchdog closes the session");
});

test("ready timeout falls back after two seconds without sleeping", () => {
  const transport = new MemoryTransport();
  let now = 0;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  let fallbackReason = "";
  client.on("bridge.fallback", (envelope) => {
    fallbackReason = String(envelope.payload.reason);
  });
  client.start("little-cubes", ["voxel-player-v1"]);

  now = 1_999;
  client.pollWatchdog();
  assert.equal(fallbackReason, "");
  now = 2_000;
  client.pollWatchdog();

  assert.equal(fallbackReason, "ready-timeout");
  assert.equal(messageAt(transport, 1).type, "bridge.fallback");
  assert.equal(messageAt(transport, 1).sessionId, "session-a");
});

test("ready modules fall back when the first authoritative state never arrives", () => {
  const transport = new MemoryTransport();
  let now = 0;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  let fallbackReason = "";
  client.on("bridge.fallback", (envelope) => {
    fallbackReason = String(envelope.payload.reason);
  });
  client.start("name-to-shop", ["shop-flight-v1"]);
  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "shop-flight-v1" });

  now = 1_999;
  client.pollWatchdog();
  assert.equal(fallbackReason, "");
  now = 2_000;
  client.pollWatchdog();

  assert.equal(client.authorityActive, false);
  assert.equal(fallbackReason, "first-state-timeout");
});

test("active authority falls back after 500 ms without state", () => {
  const transport = new MemoryTransport();
  let now = 0;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  let fallbackReason = "";
  client.on("bridge.fallback", (envelope) => {
    fallbackReason = String(envelope.payload.reason);
  });
  client.start("little-cubes", ["voxel-player-v1"]);
  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 1, { tick: 1 });
  client.activateAuthority();

  now = 499;
  client.pollWatchdog();
  assert.equal(client.authorityActive, true);
  now = 500;
  client.pollWatchdog();

  assert.equal(client.authorityActive, false);
  assert.equal(fallbackReason, "state-timeout");
});

test("fallback is sticky and rejects trailing ready, state, and fallback messages", () => {
  const transport = new MemoryTransport();
  const client = new LogicClient(transport, () => 0, sessionIds("session-a"));
  let appliedStates = 0;
  let fallbackEvents = 0;
  client.on("player.state", () => {
    appliedStates++;
    client.activateAuthority();
  });
  client.on("bridge.fallback", () => fallbackEvents++);
  client.start("little-cubes", ["voxel-player-v1"]);
  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 1, { tick: 1 });
  assert.equal(client.phase, "active");

  client.fallback("state-timeout");
  const sentAfterFallback = transport.sent.length;
  client.fallback("duplicate");
  receiveFor(transport, "session-a", "bridge.ready", 2, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 3, { tick: 3 });
  receiveFor(transport, "session-a", "bridge.fallback", 4, { reason: "late-remote" });

  assert.equal(client.phase, "fallback");
  assert.equal(client.ready, false);
  assert.equal(client.authorityActive, false);
  assert.equal(appliedStates, 1);
  assert.equal(fallbackEvents, 1);
  assert.equal(transport.sent.length, sentAfterFallback, "duplicate fallback must not emit again");
  assert.equal(client.metrics.fallbacks, 1);
  assert.equal(client.metrics.terminalInboundRejected, 3);
});

test("restart isolates old high sequences and accepts sequence zero in the new session", () => {
  const transport = new MemoryTransport();
  const client = new LogicClient(
    transport,
    () => 0,
    sessionIds("session-a", "session-b"),
  );
  let appliedStates = 0;
  client.on("player.state", () => {
    appliedStates++;
    client.activateAuthority();
  });
  client.start("little-cubes", ["voxel-player-v1"]);

  client.restart("retry");

  assert.deepEqual(messageAt(transport, 1), {
    protocol: 1,
    type: "bridge.restart",
    seq: 0,
    sessionId: "session-b",
    payload: { previousSessionId: "session-a", reason: "retry" },
  });
  assert.deepEqual(messageAt(transport, 2), {
    protocol: 1,
    type: "bridge.hello",
    seq: 1,
    sessionId: "session-b",
    payload: { gameId: "little-cubes", capabilities: ["voxel-player-v1"] },
  });

  receiveFor(transport, "session-a", "bridge.ready", 900, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 901, { tick: 901 });
  receiveFor(transport, "session-a", "bridge.fallback", 902, { reason: "old-session" });
  assert.equal(client.phase, "connecting");
  assert.equal(appliedStates, 0);

  receiveFor(transport, "session-b", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-b", "player.state", 0, { tick: 0 });
  assert.equal(client.phase, "active");
  assert.equal(appliedStates, 1);
  assert.equal(client.metrics.foreignSessionRejected, 3);
  assert.equal(client.metrics.staleInboundRejected, 0);
  assert.equal(client.metrics.restarts, 1);
});

test("repeated start uses a restart envelope and the updated hello configuration", () => {
  const transport = new MemoryTransport();
  const client = new LogicClient(
    transport,
    () => 0,
    sessionIds("session-a", "session-b"),
  );
  client.start("first-game", ["voxel-player-v1"]);

  client.start("second-game", ["shop-flight-v1"]);

  assert.deepEqual(messageAt(transport, 1), {
    protocol: 1,
    type: "bridge.restart",
    seq: 0,
    sessionId: "session-b",
    payload: { previousSessionId: "session-a", reason: "start" },
  });
  assert.deepEqual(messageAt(transport, 2), {
    protocol: 1,
    type: "bridge.hello",
    seq: 1,
    sessionId: "session-b",
    payload: { gameId: "second-game", capabilities: ["shop-flight-v1"] },
  });
});

test("foreign-session traffic cannot renew an active watchdog", () => {
  const transport = new MemoryTransport();
  let now = 0;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  client.on("player.state", () => client.activateAuthority());
  client.start("little-cubes", ["voxel-player-v1"]);
  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 1, { tick: 1 });

  now = 499;
  receiveFor(transport, "foreign-session", "player.state", 999, { tick: 999 });
  assert.equal(client.authorityActive, true);
  now = 500;
  client.pollWatchdog();

  assert.equal(client.phase, "fallback");
  assert.equal(client.metrics.foreignSessionRejected, 1);
});

test("a valid state at 499 ms renews the watchdog for another 500 ms", () => {
  const transport = new MemoryTransport();
  let now = 0;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  client.on("player.state", () => client.activateAuthority());
  client.start("little-cubes", ["voxel-player-v1"]);
  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 1, { tick: 1 });

  now = 499;
  receiveFor(transport, "session-a", "player.state", 2, { tick: 2 });
  now = 998;
  client.pollWatchdog();
  assert.equal(client.authorityActive, true);
  now = 999;
  client.pollWatchdog();
  assert.equal(client.phase, "fallback");
});

test("a state arriving at the 500 ms deadline is rejected before it can renew authority", () => {
  const transport = new MemoryTransport();
  let now = 0;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  let appliedStates = 0;
  client.on("player.state", () => {
    appliedStates++;
    client.activateAuthority();
  });
  client.start("little-cubes", ["voxel-player-v1"]);
  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 1, { tick: 1 });

  now = 500;
  receiveFor(transport, "session-a", "player.state", 2, { tick: 2 });

  assert.equal(client.phase, "fallback");
  assert.equal(appliedStates, 1);
  assert.equal(client.metrics.terminalInboundRejected, 1);
});

test("authority activation refuses a state whose deadline expires inside its handler", () => {
  const transport = new MemoryTransport();
  let now = 0;
  let activationAccepted = true;
  const client = new LogicClient(transport, () => now, sessionIds("session-a"));
  client.on("player.state", envelope => {
    if (envelope.payload.tick === 2) now = 500;
    activationAccepted = client.activateAuthority();
  });
  client.start("little-cubes", ["voxel-player-v1"]);
  receiveFor(transport, "session-a", "bridge.ready", 0, { profile: "voxel-player-v1" });
  receiveFor(transport, "session-a", "player.state", 1, { tick: 1 });

  now = 499;
  receiveFor(transport, "session-a", "player.state", 2, { tick: 2 });

  assert.equal(activationAccepted, false);
  assert.equal(client.phase, "fallback");
  assert.equal(client.authorityActive, false);
});

test("all reliable and latest-value outputs after start carry the active session", () => {
  const transport = new MemoryTransport();
  const client = new LogicClient(transport, () => 0, sessionIds("session-a"));
  client.start("little-cubes", ["voxel-player-v1"]);
  client.send("player.bootstrap", { x: 1 });
  client.sendLatest("input", "player.input", { moveX: 1 });
  client.flushLatest();
  client.fallback("done");

  assert.deepEqual(
    transport.sent.map(message => (JSON.parse(message) as { sessionId?: string }).sessionId),
    ["session-a", "session-a", "session-a", "session-a"],
  );
});

test("WebView transport reports unavailable outside a WebView2 host", () => {
  const transport = new WebViewLogicTransport({});
  assert.equal(transport.available, false);
  assert.doesNotThrow(() => transport.send("{}"));
  assert.doesNotThrow(() => transport.subscribe(() => undefined)());
});

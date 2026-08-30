import { createWebViewLogicClient } from "./client.js";
import type { LogicEnvelope } from "./protocol.js";
import {
  ReconnectBackoff,
  type ReconnectBackoffOptions,
} from "./reconnect.js";
import {
  RUNTIME_LIFECYCLE_ACK_MESSAGE,
  RUNTIME_LIFECYCLE_FEATURE,
  RUNTIME_LIFECYCLE_STATE_MESSAGE,
  RuntimeLifecycleGate,
  type RuntimeLifecycleMetrics,
  type RuntimeLifecycleState,
} from "./lifecycle.js";

export const SHOP_FLIGHT_PROFILE = "shop-flight-v1" as const;
export const SESSION_RESTART_FEATURE = "session-restart-v1" as const;

export interface ShopFlightVector {
  x: number;
  y: number;
  z: number;
}

export interface ShopFlightSnapshot {
  time: number;
  amplitude: number;
  flying: boolean;
}

export interface ShopFlightState extends ShopFlightSnapshot {
  generation: number;
  position: ShopFlightVector;
  rotation: ShopFlightVector;
  tick: number;
  ackCommandSeq: number;
}

export interface ShopFlightAuthorityClient {
  readonly ready: boolean;
  readonly sessionId: string | undefined;
  on(type: string, handler: (envelope: LogicEnvelope) => void): () => void;
  start(gameId: string, capabilities: string[]): void;
  restart(reason?: string): void;
  send(type: string, payload: object): number;
  pollWatchdog(): void;
  activateAuthority(): boolean;
  fallback(reason: string): void;
  dispose(): void;
}

export interface ShopFlightAuthorityOptions {
  client?: ShopFlightAuthorityClient;
  gameId?: string;
  getSnapshot(): ShopFlightSnapshot;
  applyState(state: ShopFlightState): void;
  runFallbackFrame(deltaTime: number): void;
  onAuthorityChange?(active: boolean, reason: string): void;
  onRuntimeLifecycle?(state: RuntimeLifecycleState): void;
  reconnect?: ReconnectBackoffOptions;
}

export interface ShopFlightAuthority {
  readonly authorityActive: boolean;
  readonly generation: number;
  readonly runtimeActive: boolean;
  readonly runtimeLifecycle: RuntimeLifecycleState | undefined;
  readonly runtimeLifecycleMetrics: RuntimeLifecycleMetrics;
  update(deltaTime: number): void;
  runRuntimeFrame(work: () => void): boolean;
  requestFlying(flying: boolean): boolean;
  reset(): void;
  dispose(): void;
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function isVector(value: unknown): value is ShopFlightVector {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const vector = value as Record<string, unknown>;
  return isFiniteNumber(vector.x) && isFiniteNumber(vector.y) && isFiniteNumber(vector.z);
}

function isFlightState(value: unknown): value is ShopFlightState {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const state = value as Record<string, unknown>;
  return Number.isInteger(state.generation)
    && (state.generation as number) >= 0
    && isFiniteNumber(state.time)
    && state.time >= 0
    && isFiniteNumber(state.amplitude)
    && state.amplitude >= 0
    && state.amplitude <= 1
    && typeof state.flying === "boolean"
    && Number.isInteger(state.tick)
    && (state.tick as number) >= 0
    && Number.isInteger(state.ackCommandSeq)
    && isVector(state.position)
    && isVector(state.rotation);
}

export function createShopFlightAuthority(options: ShopFlightAuthorityOptions): ShopFlightAuthority {
  if (!options || typeof options.getSnapshot !== "function"
    || typeof options.applyState !== "function"
    || typeof options.runFallbackFrame !== "function") {
    throw new TypeError("getSnapshot, applyState, and runFallbackFrame are required");
  }

  const ownsClient = !options.client;
  const client = options.client ?? createWebViewLogicClient();
  const gameId = options.gameId?.trim() || "name-to-shop";
  const onAuthorityChange = options.onAuthorityChange ?? (() => undefined);
  let generation = 0;
  let lastStateTick = -1;
  let authorityActive = false;
  let handshakeReady = false;
  let supportsSessionRestart = false;
  let fallbackSessionId: string | undefined;
  let fallbackSessionHandled = false;
  let disposed = false;
  const reconnect = new ReconnectBackoff(options.reconnect);
  const lifecycle = new RuntimeLifecycleGate({ onChange: options.onRuntimeLifecycle });

  const setAuthority = (active: boolean, reason: string): void => {
    if (authorityActive === active) return;
    authorityActive = active;
    onAuthorityChange(active, reason);
  };

  const sendBootstrap = (): void => {
    const snapshot = options.getSnapshot();
    const time = isFiniteNumber(snapshot?.time) && snapshot.time >= 0 ? snapshot.time : 0;
    const amplitude = isFiniteNumber(snapshot?.amplitude)
      ? Math.max(0, Math.min(1, snapshot.amplitude))
      : 0;
    client.send("flight.bootstrap", {
      generation,
      time,
      amplitude,
      flying: Boolean(snapshot?.flying),
    });
  };

  const restoreLocalAuthority = (reason: string): void => {
    if (fallbackSessionHandled && fallbackSessionId === client.sessionId) return;
    fallbackSessionHandled = true;
    fallbackSessionId = client.sessionId;
    handshakeReady = false;
    lifecycle.configure(false);
    generation++;
    lastStateTick = -1;
    let firstError: unknown;
    let hasError = false;
    try {
      setAuthority(false, reason);
    } catch (error) {
      firstError = error;
      hasError = true;
    }
    try {
      if (supportsSessionRestart) reconnect.schedule(reason);
      else reconnect.cancel();
    } catch (error) {
      if (!hasError) {
        firstError = error;
        hasError = true;
      }
    }
    if (hasError) throw firstError;
  };

  const enterTerminalFallback = (reason: string): void => {
    try {
      client.fallback(reason);
    } catch {
      // The original JavaScript simulation must remain authoritative even if
      // the transport or a fallback observer fails while closing the session.
    }
    try {
      restoreLocalAuthority(reason);
    } catch {
      // Local authority state is reset before user callbacks are invoked.
    }
  };

  const unsubscribers = [
    client.on("bridge.ready", envelope => {
      if (disposed) return;
      if (envelope.payload.profile !== SHOP_FLIGHT_PROFILE) {
        enterTerminalFallback("profile-mismatch");
        return;
      }
      fallbackSessionHandled = false;
      handshakeReady = true;
      supportsSessionRestart = Array.isArray(envelope.payload.features)
        && envelope.payload.features.includes(SESSION_RESTART_FEATURE);
      lifecycle.configure(Array.isArray(envelope.payload.features)
        && envelope.payload.features.includes(RUNTIME_LIFECYCLE_FEATURE));
      sendBootstrap();
    }),
    client.on("flight.state", envelope => {
      if (disposed || !handshakeReady || !client.ready) return;
      const stateGeneration = (envelope.payload as { generation?: unknown }).generation;
      if (Number.isInteger(stateGeneration) && stateGeneration !== generation) return;
      if (!isFlightState(envelope.payload)) {
        enterTerminalFallback("invalid-flight-state");
        return;
      }
      if (envelope.payload.tick <= lastStateTick) return;
      if (!client.activateAuthority()) return;
      lastStateTick = envelope.payload.tick;
      try {
        options.applyState(envelope.payload);
        setAuthority(true, "state");
      } catch {
        enterTerminalFallback("state-apply-error");
        return;
      }
      reconnect.success();
    }),
    client.on("bridge.fallback", envelope => {
      const reason = typeof envelope.payload.reason === "string"
        && envelope.payload.reason.trim().length > 0
        ? envelope.payload.reason.trim()
        : "fallback";
      restoreLocalAuthority(reason);
    }),
    client.on(RUNTIME_LIFECYCLE_STATE_MESSAGE, envelope => {
      if (disposed || !handshakeReady || !client.ready) return;
      const state = lifecycle.accept(envelope.payload);
      if (state === undefined) return;
      client.send(RUNTIME_LIFECYCLE_ACK_MESSAGE, {
        revision: state.revision,
        active: state.active,
      });
    }),
  ];

  client.start(gameId, [
    SHOP_FLIGHT_PROFILE,
    SESSION_RESTART_FEATURE,
    RUNTIME_LIFECYCLE_FEATURE,
  ]);

  return {
    get authorityActive(): boolean { return authorityActive; },
    get generation(): number { return generation; },
    get runtimeActive(): boolean { return lifecycle.active; },
    get runtimeLifecycle(): RuntimeLifecycleState | undefined { return lifecycle.state; },
    get runtimeLifecycleMetrics(): RuntimeLifecycleMetrics { return lifecycle.metrics; },
    update(deltaTime: number): void {
      if (disposed) return;
      client.pollWatchdog();
      const reconnectReason = reconnect.poll();
      if (reconnectReason !== undefined) {
        fallbackSessionHandled = false;
        handshakeReady = false;
        client.restart(reconnectReason);
      }
      if (!authorityActive) options.runFallbackFrame(deltaTime);
    },
    runRuntimeFrame(work: () => void): boolean {
      if (typeof work !== "function") throw new TypeError("runtime frame work must be a function");
      return lifecycle.run(work);
    },
    requestFlying(flying: boolean): boolean {
      if (!disposed && client.ready) {
        client.send("flight.command", { generation, flying: Boolean(flying) });
      }
      return authorityActive;
    },
    reset(): void {
      if (disposed) return;
      generation++;
      lastStateTick = -1;
      setAuthority(false, "reset");
      if (client.ready && handshakeReady) sendBootstrap();
    },
    dispose(): void {
      if (disposed) return;
      disposed = true;
      reconnect.cancel();
      lifecycle.configure(false);
      let firstError: unknown;
      let hasError = false;
      const clean = (operation: () => void): void => {
        try {
          operation();
        } catch (error) {
          if (!hasError) {
            firstError = error;
            hasError = true;
          }
        }
      };
      clean(() => setAuthority(false, "dispose"));
      for (const unsubscribe of unsubscribers) clean(unsubscribe);
      if (ownsClient) clean(() => client.dispose());
      if (hasError) throw firstError;
    },
  };
}

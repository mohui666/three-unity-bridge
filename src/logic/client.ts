import {
  encodeLogicEnvelope,
  type LogicEnvelope,
  parseLogicEnvelope,
} from "./protocol.js";

export interface LogicTransport {
  readonly available: boolean;
  send(message: string): void;
  subscribe(handler: (message: string) => void): () => void;
}

export interface LogicClientMetrics {
  outboundMessages: number;
  outboundCharacters: number;
  inboundMessages: number;
  inboundCharacters: number;
  latestValuesCoalesced: number;
  staleInboundRejected: number;
  foreignSessionRejected: number;
  terminalInboundRejected: number;
  phaseInboundRejected: number;
  protocolErrors: number;
  fallbacks: number;
  restarts: number;
  pendingLatestStreams: number;
}

export type LogicClientPhase = "idle" | "connecting" | "ready" | "active" | "fallback" | "disposed";

type MessageHandler = (envelope: LogicEnvelope) => void;

interface PendingMessage {
  type: string;
  seq: number;
  payload: object;
  sessionId?: string;
}

interface WebViewEvent {
  data: unknown;
}

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: "message", handler: (event: WebViewEvent) => void): void;
  removeEventListener?(type: "message", handler: (event: WebViewEvent) => void): void;
}

let fallbackSessionCounter = 0;
const SESSION_ID_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

function encodeSessionBytes(bytes: Uint8Array): string {
  let encoded = "";
  let accumulator = 0;
  let availableBits = 0;
  for (const byte of bytes) {
    accumulator = (accumulator << 8) | byte;
    availableBits += 8;
    while (availableBits >= 6) {
      availableBits -= 6;
      encoded += SESSION_ID_ALPHABET[(accumulator >>> availableBits) & 63];
    }
  }
  if (availableBits > 0) {
    encoded += SESSION_ID_ALPHABET[(accumulator << (6 - availableBits)) & 63];
  }
  return encoded;
}

function createRandomSessionId(): string {
  const cryptoScope = globalThis.crypto;
  if (typeof cryptoScope?.getRandomValues === "function") {
    const bytes = new Uint8Array(10);
    cryptoScope.getRandomValues(bytes);
    return encodeSessionBytes(bytes);
  }

  fallbackSessionCounter++;
  const time = Date.now().toString(36).slice(-8).padStart(8, "0");
  const counter = fallbackSessionCounter.toString(36).slice(-3).padStart(3, "0");
  const random = Math.random().toString(36).slice(2, 7).padEnd(5, "0");
  return `${time}${counter}${random}`;
}

function validateGeneratedSessionId(sessionId: string, previousSessionId?: string): string {
  if (typeof sessionId !== "string"
    || sessionId.length === 0
    || sessionId.length > 128
    || sessionId.trim() !== sessionId) {
    throw new Error("Logic session id factory must return a non-empty trimmed string of at most 128 characters");
  }
  if (sessionId === previousSessionId) {
    throw new Error("Logic session id factory must return a new id for every session");
  }
  return sessionId;
}

function findWebViewBridge(scope: unknown): WebViewBridge | undefined {
  if (typeof scope !== "object" || scope === null) return undefined;
  const chrome = (scope as { chrome?: unknown }).chrome;
  if (typeof chrome !== "object" || chrome === null) return undefined;
  const webview = (chrome as { webview?: unknown }).webview;
  if (typeof webview !== "object" || webview === null) return undefined;
  const candidate = webview as Partial<WebViewBridge>;
  if (typeof candidate.postMessage !== "function" || typeof candidate.addEventListener !== "function") {
    return undefined;
  }
  return candidate as WebViewBridge;
}

export class WebViewLogicTransport implements LogicTransport {
  private readonly webview?: WebViewBridge;

  constructor(scope: unknown = globalThis) {
    this.webview = findWebViewBridge(scope);
  }

  get available(): boolean {
    return this.webview !== undefined;
  }

  send(message: string): void {
    if (!this.webview) return;
    this.webview.postMessage(JSON.parse(message) as unknown);
  }

  subscribe(handler: (message: string) => void): () => void {
    if (!this.webview) return () => undefined;
    const listener = (event: WebViewEvent): void => {
      handler(typeof event.data === "string" ? event.data : JSON.stringify(event.data));
    };
    this.webview.addEventListener("message", listener);
    return () => this.webview?.removeEventListener?.("message", listener);
  }
}

export class LogicClient {
  private readonly handlers = new Map<string, Set<MessageHandler>>();
  private readonly latest = new Map<string, PendingMessage>();
  private readonly receivedSequences = new Map<string, number>();
  private readonly unsubscribeTransport: () => void;
  private outboundSequence = 0;
  private _sessionId: string | undefined;
  private _phase: LogicClientPhase = "idle";
  private gameId: string | undefined;
  private capabilities: string[] | undefined;
  private startedAt: number | undefined;
  private readyAt: number | undefined;
  private lastStateAt: number | undefined;
  private watchdogEnabled = false;
  private outboundMessages = 0;
  private outboundCharacters = 0;
  private inboundMessages = 0;
  private inboundCharacters = 0;
  private latestValuesCoalesced = 0;
  private staleInboundRejected = 0;
  private foreignSessionRejected = 0;
  private terminalInboundRejected = 0;
  private phaseInboundRejected = 0;
  private protocolErrors = 0;
  private fallbacks = 0;
  private restarts = 0;

  constructor(
    private readonly transport: LogicTransport,
    private readonly now: () => number = () => performance.now(),
    private readonly sessionIdFactory: () => string = createRandomSessionId,
  ) {
    this.unsubscribeTransport = transport.subscribe((message) => this.receive(message));
  }

  get ready(): boolean {
    return this._phase === "ready" || this._phase === "active";
  }

  get authorityActive(): boolean {
    return this._phase === "active";
  }

  get sessionId(): string | undefined {
    return this._sessionId;
  }

  get phase(): LogicClientPhase {
    return this._phase;
  }

  get metrics(): LogicClientMetrics {
    return {
      outboundMessages: this.outboundMessages,
      outboundCharacters: this.outboundCharacters,
      inboundMessages: this.inboundMessages,
      inboundCharacters: this.inboundCharacters,
      latestValuesCoalesced: this.latestValuesCoalesced,
      staleInboundRejected: this.staleInboundRejected,
      foreignSessionRejected: this.foreignSessionRejected,
      terminalInboundRejected: this.terminalInboundRejected,
      phaseInboundRejected: this.phaseInboundRejected,
      protocolErrors: this.protocolErrors,
      fallbacks: this.fallbacks,
      restarts: this.restarts,
      pendingLatestStreams: this.latest.size,
    };
  }

  start(gameId: string, capabilities: string[]): void {
    if (this._phase === "disposed") return;
    this.gameId = gameId;
    this.capabilities = capabilities.slice();
    if (this._sessionId !== undefined) {
      this.restart("start");
      return;
    }
    this.openSession();

    if (!this.transport.available) {
      this.fallback("transport-unavailable");
      return;
    }
    this.sendHello();
  }

  restart(reason = "restart"): void {
    if (this._phase === "disposed") return;
    if (this.gameId === undefined || this.capabilities === undefined || this._sessionId === undefined) {
      throw new Error("LogicClient must be started before it can restart");
    }

    const previousSessionId = this._sessionId;
    this.openSession();
    this.restarts++;
    if (!this.transport.available) {
      this.fallback("transport-unavailable");
      return;
    }

    this.send("bridge.restart", {
      previousSessionId,
      reason: typeof reason === "string" && reason.trim().length > 0 ? reason : "restart",
    });
    this.sendHello();
  }

  send<TPayload extends object>(type: string, payload: TPayload): number {
    const seq = this.outboundSequence++;
    if (this.transport.available) {
      this.transmit(this.encode(type, seq, payload, this._sessionId));
    }
    return seq;
  }

  sendLatest<TPayload extends object>(stream: string, type: string, payload: TPayload): number {
    const seq = this.outboundSequence++;
    if (this.latest.has(stream)) this.latestValuesCoalesced++;
    this.latest.set(stream, { type, seq, payload, sessionId: this._sessionId });
    return seq;
  }

  flushLatest(): void {
    if (!this.transport.available) {
      this.latest.clear();
      return;
    }
    for (const message of this.latest.values()) {
      this.transmit(this.encode(message.type, message.seq, message.payload, message.sessionId));
    }
    this.latest.clear();
  }

  pollWatchdog(): void {
    this.expireIfNeeded(this.now());
  }

  on(type: string, handler: MessageHandler): () => void {
    let handlers = this.handlers.get(type);
    if (!handlers) {
      handlers = new Set<MessageHandler>();
      this.handlers.set(type, handlers);
    }
    handlers.add(handler);
    return () => {
      handlers?.delete(handler);
      if (handlers?.size === 0) this.handlers.delete(type);
    };
  }

  activateAuthority(): boolean {
    if (this._phase !== "ready" && this._phase !== "active") return false;
    const now = this.now();
    if (this.expireIfNeeded(now)) return false;
    this._phase = "active";
    this.lastStateAt = now;
    return true;
  }

  fallback(reason: string): void {
    if (this._phase === "fallback" || this._phase === "disposed") return;
    const seq = this.outboundSequence++;
    const envelope: LogicEnvelope<{ reason: string }> = {
      protocol: 1,
      type: "bridge.fallback",
      seq,
      ...(this._sessionId === undefined ? {} : { sessionId: this._sessionId }),
      payload: { reason },
    };
    const message = this.encode(envelope.type, envelope.seq, envelope.payload, envelope.sessionId);
    this.closeSession();
    if (this.transport.available) {
      this.transmit(message);
    }
    this.dispatch(envelope);
  }

  dispose(): void {
    if (this._phase === "disposed") return;
    this._phase = "disposed";
    this.watchdogEnabled = false;
    this.unsubscribeTransport();
    this.handlers.clear();
    this.latest.clear();
  }

  private receive(message: string): void {
    this.inboundMessages++;
    this.inboundCharacters += message.length;
    const now = this.now();
    this.expireIfNeeded(now);
    let envelope: LogicEnvelope;
    try {
      envelope = parseLogicEnvelope(message);
    } catch (error) {
      this.protocolErrors++;
      return;
    }

    if (envelope.sessionId !== this._sessionId) {
      this.foreignSessionRejected++;
      return;
    }
    if (this._phase === "fallback" || this._phase === "disposed") {
      this.terminalInboundRejected++;
      return;
    }
    if (envelope.type === "bridge.ready" && this._phase !== "connecting") {
      this.phaseInboundRejected++;
      return;
    }
    if (envelope.type.endsWith(".state")
      && this._phase !== "ready"
      && this._phase !== "active") {
      this.phaseInboundRejected++;
      return;
    }

    const previousSequence = this.receivedSequences.get(envelope.type);
    if (previousSequence !== undefined && envelope.seq <= previousSequence) {
      this.staleInboundRejected++;
      return;
    }
    this.receivedSequences.set(envelope.type, envelope.seq);

    if (envelope.type === "bridge.ready") {
      this._phase = "ready";
      this.readyAt = now;
    } else if (envelope.type === "bridge.fallback") {
      this.closeSession();
    }
    this.dispatch(envelope);
  }

  private openSession(): void {
    const nextSessionId = validateGeneratedSessionId(this.sessionIdFactory(), this._sessionId);
    this._sessionId = nextSessionId;
    this._phase = "connecting";
    this.outboundSequence = 0;
    this.latest.clear();
    this.receivedSequences.clear();
    this.readyAt = undefined;
    this.lastStateAt = undefined;
    this.startedAt = this.now();
    this.watchdogEnabled = this.transport.available;
  }

  private sendHello(): void {
    this.send("bridge.hello", {
      gameId: this.gameId ?? "",
      capabilities: this.capabilities?.slice() ?? [],
    });
  }

  private closeSession(): void {
    this._phase = "fallback";
    this.watchdogEnabled = false;
    this.startedAt = undefined;
    this.readyAt = undefined;
    this.lastStateAt = undefined;
    this.latest.clear();
    this.fallbacks++;
  }

  private expireIfNeeded(now: number): boolean {
    if (!this.watchdogEnabled) return false;
    if (this._phase === "connecting"
      && this.startedAt !== undefined
      && now - this.startedAt >= 2_000) {
      this.fallback("ready-timeout");
      return true;
    }
    if (this._phase === "ready"
      && this.readyAt !== undefined
      && now - this.readyAt >= 2_000) {
      this.fallback("first-state-timeout");
      return true;
    }
    if (this._phase === "active"
      && this.lastStateAt !== undefined
      && now - this.lastStateAt >= 500) {
      this.fallback("state-timeout");
      return true;
    }
    return false;
  }

  private encode<TPayload extends object>(
    type: string,
    seq: number,
    payload: TPayload,
    sessionId: string | undefined,
  ): string {
    return sessionId === undefined
      ? encodeLogicEnvelope(type, seq, payload)
      : encodeLogicEnvelope(type, seq, payload, sessionId);
  }

  private transmit(message: string): void {
    this.transport.send(message);
    this.outboundMessages++;
    this.outboundCharacters += message.length;
  }

  private dispatch(envelope: LogicEnvelope): void {
    for (const handler of this.handlers.get(envelope.type) ?? []) handler(envelope);
  }
}

export function createWebViewLogicClient(
  now?: () => number,
  scope: unknown = globalThis,
): LogicClient {
  return new LogicClient(new WebViewLogicTransport(scope), now);
}

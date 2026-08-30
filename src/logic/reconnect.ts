export interface ReconnectBackoffOptions {
  now?: () => number;
  initialDelayMs?: number;
  maxDelayMs?: number;
  maxAttempts?: number;
  retryable?: (reason: string) => boolean;
}

export interface ReconnectBackoffSnapshot {
  attempts: number;
  scheduled: boolean;
  exhausted: boolean;
  reason?: string;
  nextAttemptAt?: number;
}

const DEFAULT_INITIAL_DELAY_MS = 250;
const DEFAULT_MAX_DELAY_MS = 4_000;
const DEFAULT_MAX_ATTEMPTS = 5;

export function isRetryableReconnectReason(reason: string): boolean {
  const normalized = reason.trim().toLowerCase();
  if (normalized === "transport-unavailable") return false;
  if (normalized.includes("profile")) return false;
  if (normalized.includes("capability")) return false;
  if (normalized === "state-apply-error") return false;
  if (normalized === "invalid-state") return false;
  if (normalized.startsWith("invalid-") && normalized.endsWith("-state")) return false;
  return true;
}

/**
 * Deterministic reconnect scheduler. It owns no timer and performs no I/O;
 * callers keep their original JavaScript simulation running and poll it from
 * the existing game loop.
 */
export class ReconnectBackoff {
  private readonly now: () => number;
  private readonly initialDelayMs: number;
  private readonly maxDelayMs: number;
  private readonly maxAttempts: number;
  private readonly retryable: (reason: string) => boolean;
  private attempts = 0;
  private nextAttemptAt: number | undefined;
  private pendingReason: string | undefined;

  constructor(options: ReconnectBackoffOptions = {}) {
    this.now = options.now ?? (() => performance.now());
    this.initialDelayMs = validateDelay(
      options.initialDelayMs ?? DEFAULT_INITIAL_DELAY_MS,
      "initialDelayMs",
    );
    this.maxDelayMs = validateDelay(
      options.maxDelayMs ?? DEFAULT_MAX_DELAY_MS,
      "maxDelayMs",
    );
    if (this.maxDelayMs < this.initialDelayMs) {
      throw new RangeError("maxDelayMs must be greater than or equal to initialDelayMs");
    }
    this.maxAttempts = options.maxAttempts ?? DEFAULT_MAX_ATTEMPTS;
    if (!Number.isInteger(this.maxAttempts) || this.maxAttempts <= 0) {
      throw new RangeError("maxAttempts must be a positive integer");
    }
    this.retryable = options.retryable ?? isRetryableReconnectReason;
  }

  get snapshot(): ReconnectBackoffSnapshot {
    return {
      attempts: this.attempts,
      scheduled: this.nextAttemptAt !== undefined,
      exhausted: this.attempts >= this.maxAttempts,
      ...(this.pendingReason === undefined ? {} : { reason: this.pendingReason }),
      ...(this.nextAttemptAt === undefined ? {} : { nextAttemptAt: this.nextAttemptAt }),
    };
  }

  schedule(reason: string): boolean {
    if (typeof reason !== "string" || reason.trim().length === 0) {
      throw new TypeError("Reconnect reason must be a non-empty string");
    }
    if (!this.retryable(reason) || this.attempts >= this.maxAttempts) {
      this.cancel();
      return false;
    }
    if (this.nextAttemptAt !== undefined) return true;

    const at = this.now();
    if (!Number.isFinite(at)) throw new RangeError("Reconnect time must be finite");
    const delay = Math.min(this.maxDelayMs, this.initialDelayMs * (2 ** this.attempts));
    this.pendingReason = reason;
    this.nextAttemptAt = at + delay;
    return true;
  }

  /** Returns the scheduled fallback reason once an attempt becomes due. */
  poll(): string | undefined {
    if (this.nextAttemptAt === undefined) return undefined;
    const at = this.now();
    if (!Number.isFinite(at)) throw new RangeError("Reconnect time must be finite");
    if (at < this.nextAttemptAt) return undefined;

    const reason = this.pendingReason;
    this.nextAttemptAt = undefined;
    this.pendingReason = undefined;
    this.attempts++;
    return reason;
  }

  success(): void {
    this.attempts = 0;
    this.cancel();
  }

  cancel(): void {
    this.nextAttemptAt = undefined;
    this.pendingReason = undefined;
  }
}

function validateDelay(value: number, name: string): number {
  if (!Number.isFinite(value) || value < 0) {
    throw new RangeError(`${name} must be a non-negative finite number`);
  }
  return value;
}

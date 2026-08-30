export interface RealtimeInputGateMetrics {
  sampled: number;
  emitted: number;
  changed: number;
  heartbeats: number;
  suppressed: number;
  rateLimited: number;
  pending: boolean;
}

export interface RealtimeInputGateOptions<TValue extends object> {
  equals(left: TValue, right: TValue): boolean;
  merge?(pending: TValue, next: TValue): TValue;
  isUrgent?(lastEmitted: TValue, next: TValue): boolean;
  now?: () => number;
  minimumIntervalMs?: number;
  heartbeatMs?: number;
}

/**
 * Change-aware rate gate for replaceable realtime inputs.
 *
 * The first value and urgent transitions are emitted immediately. Continuous
 * analog changes are reduced to a bounded rate, while an unchanged heartbeat
 * keeps the authority side informed that the producer is still alive. A merge
 * callback can retain one-frame edges while several samples are coalesced.
 */
export class RealtimeInputGate<TValue extends object> {
  private readonly equals: (left: TValue, right: TValue) => boolean;
  private readonly merge: (pending: TValue, next: TValue) => TValue;
  private readonly isUrgent: (lastEmitted: TValue, next: TValue) => boolean;
  private readonly now: () => number;
  private readonly minimumIntervalMs: number;
  private readonly heartbeatMs: number;
  private hasEmitted = false;
  private hasPending = false;
  private lastEmitted!: TValue;
  private pending!: TValue;
  private lastEmissionAt = 0;
  private sampled = 0;
  private emitted = 0;
  private changed = 0;
  private heartbeats = 0;
  private suppressed = 0;
  private rateLimited = 0;

  constructor(options: RealtimeInputGateOptions<TValue>) {
    if (!options || typeof options.equals !== "function") {
      throw new TypeError("RealtimeInputGate requires an equals function");
    }
    this.equals = options.equals;
    this.merge = options.merge ?? ((_pending, next) => next);
    this.isUrgent = options.isUrgent ?? (() => false);
    this.now = options.now ?? (() => performance.now());
    this.minimumIntervalMs = validateInterval(
      options.minimumIntervalMs ?? 1000 / 60,
      "minimumIntervalMs",
      true,
    );
    this.heartbeatMs = validateInterval(options.heartbeatMs ?? 250, "heartbeatMs", false);
    if (this.heartbeatMs < this.minimumIntervalMs) {
      throw new RangeError("heartbeatMs must be greater than or equal to minimumIntervalMs");
    }
  }

  get metrics(): RealtimeInputGateMetrics {
    return {
      sampled: this.sampled,
      emitted: this.emitted,
      changed: this.changed,
      heartbeats: this.heartbeats,
      suppressed: this.suppressed,
      rateLimited: this.rateLimited,
      pending: this.hasPending,
    };
  }

  offer(value: TValue, at = this.now()): TValue | undefined {
    if (!Number.isFinite(at)) {
      throw new RangeError("Input sample time must be finite");
    }
    this.sampled++;
    if (!this.hasEmitted) {
      return this.emit(value, at, false);
    }

    const candidate = this.hasPending ? this.merge(this.pending, value) : value;
    if (this.equals(this.lastEmitted, candidate)) {
      this.hasPending = false;
      if (at - this.lastEmissionAt >= this.heartbeatMs) {
        return this.emit(value, at, true);
      }
      this.suppressed++;
      return undefined;
    }

    this.pending = candidate;
    this.hasPending = true;
    if (!this.isUrgent(this.lastEmitted, candidate)
      && at - this.lastEmissionAt < this.minimumIntervalMs) {
      this.suppressed++;
      this.rateLimited++;
      return undefined;
    }
    return this.emit(candidate, at, false);
  }

  reset(): void {
    this.hasEmitted = false;
    this.hasPending = false;
    this.lastEmissionAt = 0;
  }

  private emit(value: TValue, at: number, heartbeat: boolean): TValue {
    this.lastEmitted = value;
    this.lastEmissionAt = at;
    this.hasEmitted = true;
    this.hasPending = false;
    this.emitted++;
    if (heartbeat) this.heartbeats++;
    else this.changed++;
    return value;
  }
}

function validateInterval(value: number, name: string, allowZero: boolean): number {
  if (!Number.isFinite(value) || (allowZero ? value < 0 : value <= 0)) {
    throw new RangeError(`${name} must be a ${allowZero ? "non-negative" : "positive"} finite number`);
  }
  return value;
}

export interface StateInterpolatorMetrics {
  receivedStates: number;
  sampledFrames: number;
  interpolatedFrames: number;
  heldFrames: number;
  staleStatesRejected: number;
  bufferedStates: number;
}

export interface StateInterpolatorOptions<TState> {
  interpolate(from: TState, to: TState, alpha: number): TState;
  sequence?(state: TState): number;
  now?: () => number;
  delayMs?: number;
  maxBufferedStates?: number;
}

interface TimedState<TState> {
  state: TState;
  receivedAt: number;
}

/**
 * Small arrival-time interpolation buffer for Unity-authoritative snapshots.
 * It deliberately holds rather than extrapolates beyond the newest state so a
 * renderer cannot invent movement through authoritative collision boundaries.
 */
export class StateInterpolator<TState> {
  private readonly interpolateState: (from: TState, to: TState, alpha: number) => TState;
  private readonly sequence?: (state: TState) => number;
  private readonly now: () => number;
  private readonly maxBufferedStates: number;
  private readonly states: TimedState<TState>[] = [];
  private delayMs: number;
  private lastSequence: number | undefined;
  private receivedStates = 0;
  private sampledFrames = 0;
  private interpolatedFrames = 0;
  private heldFrames = 0;
  private staleStatesRejected = 0;

  constructor(options: StateInterpolatorOptions<TState>) {
    if (!options || typeof options.interpolate !== "function") {
      throw new TypeError("StateInterpolator requires an interpolate function");
    }
    this.interpolateState = options.interpolate;
    this.sequence = options.sequence;
    this.now = options.now ?? (() => performance.now());
    this.delayMs = validateDelay(options.delayMs ?? 40);
    this.maxBufferedStates = options.maxBufferedStates ?? 8;
    if (!Number.isInteger(this.maxBufferedStates) || this.maxBufferedStates < 2) {
      throw new RangeError("maxBufferedStates must be an integer of at least two");
    }
  }

  get metrics(): StateInterpolatorMetrics {
    return {
      receivedStates: this.receivedStates,
      sampledFrames: this.sampledFrames,
      interpolatedFrames: this.interpolatedFrames,
      heldFrames: this.heldFrames,
      staleStatesRejected: this.staleStatesRejected,
      bufferedStates: this.states.length,
    };
  }

  setDelayMs(value: number): void {
    this.delayMs = validateDelay(value);
  }

  push(state: TState, receivedAt = this.now()): boolean {
    if (!Number.isFinite(receivedAt)) {
      throw new RangeError("State receive time must be finite");
    }
    if (this.sequence) {
      const sequence = this.sequence(state);
      if (!Number.isFinite(sequence)) {
        throw new RangeError("State sequence must be finite");
      }
      if (this.lastSequence !== undefined && sequence <= this.lastSequence) {
        this.staleStatesRejected++;
        return false;
      }
      this.lastSequence = sequence;
    }

    const previousTime = this.states.at(-1)?.receivedAt;
    const monotonicTime = previousTime === undefined
      ? receivedAt
      : Math.max(receivedAt, previousTime + 0.001);
    this.states.push({ state, receivedAt: monotonicTime });
    while (this.states.length > this.maxBufferedStates) this.states.shift();
    this.receivedStates++;
    return true;
  }

  sample(at = this.now()): TState | undefined {
    if (!Number.isFinite(at)) {
      throw new RangeError("State sample time must be finite");
    }
    if (this.states.length === 0) return undefined;
    this.sampledFrames++;
    if (this.delayMs === 0 || this.states.length === 1) {
      this.heldFrames++;
      return this.states.at(-1)?.state;
    }

    const target = at - this.delayMs;
    while (this.states.length > 2 && this.states[1].receivedAt <= target) {
      this.states.shift();
    }
    const first = this.states[0];
    if (target <= first.receivedAt) {
      this.heldFrames++;
      return first.state;
    }
    const second = this.states[1];
    if (!second || target >= second.receivedAt) {
      this.heldFrames++;
      return this.states.at(-1)?.state;
    }

    const alpha = (target - first.receivedAt) / (second.receivedAt - first.receivedAt);
    this.interpolatedFrames++;
    return this.interpolateState(first.state, second.state, Math.max(0, Math.min(1, alpha)));
  }

  clear(): void {
    this.states.length = 0;
    this.lastSequence = undefined;
  }
}

function validateDelay(value: number): number {
  if (!Number.isFinite(value) || value < 0) {
    throw new RangeError("Interpolation delay must be a non-negative finite number");
  }
  return value;
}

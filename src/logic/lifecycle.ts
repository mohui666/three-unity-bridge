export const RUNTIME_LIFECYCLE_FEATURE = "runtime-lifecycle-v1" as const;
export const RUNTIME_LIFECYCLE_STATE_MESSAGE = "runtime.lifecycle.state" as const;
export const RUNTIME_LIFECYCLE_ACK_MESSAGE = "runtime.lifecycle.ack" as const;

export interface RuntimeLifecycleState {
  focused: boolean;
  paused: boolean;
  active: boolean;
  revision: number;
}

export interface RuntimeLifecycleMetrics {
  accepted: number;
  staleRejected: number;
  invalidRejected: number;
  unsupportedRejected: number;
  callbackErrors: number;
  activeFrames: number;
  suspendedFrames: number;
}

export interface RuntimeLifecycleGateOptions {
  onChange?(state: RuntimeLifecycleState): void;
}

function decodeRuntimeLifecycleState(payload: unknown): RuntimeLifecycleState | undefined {
  if (typeof payload !== "object" || payload === null || Array.isArray(payload)) return undefined;
  const value = payload as Record<string, unknown>;
  if (typeof value.focused !== "boolean"
    || typeof value.paused !== "boolean"
    || typeof value.active !== "boolean"
    || !Number.isInteger(value.revision)
    || (value.revision as number) < 0
    || value.active !== (value.focused && !value.paused)) {
    return undefined;
  }
  return {
    focused: value.focused,
    paused: value.paused,
    active: value.active,
    revision: value.revision as number,
  };
}

/**
 * Applies Unity's negotiated Player lifecycle without relying on WebView
 * visibility events. Unsupported, malformed, or callback-failing integrations
 * stay active so the original browser game remains the safe authority.
 */
export class RuntimeLifecycleGate {
  private supported = false;
  private _state: RuntimeLifecycleState | undefined;
  private accepted = 0;
  private staleRejected = 0;
  private invalidRejected = 0;
  private unsupportedRejected = 0;
  private callbackErrors = 0;
  private activeFrames = 0;
  private suspendedFrames = 0;

  constructor(private readonly options: RuntimeLifecycleGateOptions = {}) {}

  get active(): boolean {
    return this._state?.active ?? true;
  }

  get state(): RuntimeLifecycleState | undefined {
    return this._state === undefined ? undefined : { ...this._state };
  }

  get metrics(): RuntimeLifecycleMetrics {
    return {
      accepted: this.accepted,
      staleRejected: this.staleRejected,
      invalidRejected: this.invalidRejected,
      unsupportedRejected: this.unsupportedRejected,
      callbackErrors: this.callbackErrors,
      activeFrames: this.activeFrames,
      suspendedFrames: this.suspendedFrames,
    };
  }

  configure(supported: boolean): void {
    const restore = this._state !== undefined && !this._state.active;
    const restoreRevision = this._state?.revision ?? 0;
    this.supported = supported;
    this._state = undefined;
    if (restore && !this.notify({ focused: true, paused: false, active: true, revision: restoreRevision })) {
      this.supported = false;
    }
  }

  accept(payload: unknown): RuntimeLifecycleState | undefined {
    if (!this.supported) {
      this.unsupportedRejected++;
      return undefined;
    }
    const state = decodeRuntimeLifecycleState(payload);
    if (state === undefined) {
      this.invalidRejected++;
      return undefined;
    }
    if (this._state !== undefined && state.revision <= this._state.revision) {
      this.staleRejected++;
      return undefined;
    }

    this._state = state;
    this.accepted++;
    if (!this.notify(state)) {
      // A consumer callback is optional optimization code. If it fails, restore
      // the original browser behavior instead of freezing the game.
      this.supported = false;
      this._state = undefined;
      return undefined;
    }
    return { ...state };
  }

  run(work: () => void): boolean {
    if (!this.active) {
      this.suspendedFrames++;
      return false;
    }
    this.activeFrames++;
    work();
    return true;
  }

  private notify(state: RuntimeLifecycleState): boolean {
    try {
      this.options.onChange?.({ ...state });
      return true;
    } catch {
      this.callbackErrors++;
      return false;
    }
  }
}

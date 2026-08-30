export const LOGIC_PROTOCOL_VERSION = 1 as const;

export interface LogicEnvelope<TPayload extends object = Record<string, unknown>> {
  protocol: typeof LOGIC_PROTOCOL_VERSION;
  type: string;
  seq: number;
  sessionId?: string;
  payload: TPayload;
}

function isObjectPayload(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function validateLogicEnvelope(value: unknown): LogicEnvelope {
  if (!isObjectPayload(value)) {
    throw new Error("Logic message must be an object envelope");
  }

  if (value.protocol !== LOGIC_PROTOCOL_VERSION) {
    throw new Error(`Unsupported logic protocol ${String(value.protocol)}`);
  }
  if (typeof value.type !== "string" || value.type.trim().length === 0) {
    throw new Error("Logic envelope requires a non-empty type");
  }
  if (!Number.isInteger(value.seq) || (value.seq as number) < 0) {
    throw new Error("Logic envelope requires a non-negative integer seq");
  }
  if (Object.prototype.hasOwnProperty.call(value, "sessionId")) {
    if (typeof value.sessionId !== "string"
      || value.sessionId.length === 0
      || value.sessionId.length > 128
      || value.sessionId.trim() !== value.sessionId) {
      throw new Error("Logic envelope sessionId must be a non-empty trimmed string of at most 128 characters");
    }
  }
  if (!isObjectPayload(value.payload)) {
    throw new Error("Logic envelope requires an object payload");
  }

  return value as unknown as LogicEnvelope;
}

export function parseLogicEnvelope(text: string): LogicEnvelope {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new Error(`Invalid logic envelope JSON: ${detail}`);
  }
  return validateLogicEnvelope(parsed);
}

export function encodeLogicEnvelope<TPayload extends object>(
  type: string,
  seq: number,
  payload: TPayload,
  sessionId?: string,
): string {
  const hasSessionId = arguments.length >= 4;
  const envelope: Record<string, unknown> = {
    protocol: LOGIC_PROTOCOL_VERSION,
    ...(hasSessionId ? { sessionId } : {}),
    type,
    seq,
    payload,
  };
  return JSON.stringify(validateLogicEnvelope(envelope));
}

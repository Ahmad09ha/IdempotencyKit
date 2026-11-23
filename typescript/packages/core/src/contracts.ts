import type { IdempotencyRecordStates } from "./states.js";

export type IdempotencyRecordState =
  (typeof IdempotencyRecordStates)[keyof typeof IdempotencyRecordStates];

/** Descriptive request identity; scoping/fingerprinting are not yet implemented. */
export interface IdempotencyRequestIdentity {
  readonly namespace: string;
  readonly operationId: string;
  readonly principalId: string;
  readonly clientKey: string;
  readonly requestFingerprint: string;
  readonly tenantId?: string;
}

/** Planned bounded replayable response; no serializer or header filter exists yet. */
export interface StoredHttpResponse {
  readonly statusCode: number;
  readonly contentType: string;
  readonly body: Uint8Array;
  readonly headers: Readonly<Record<string, string>>;
}

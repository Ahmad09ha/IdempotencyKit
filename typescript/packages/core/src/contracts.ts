import type { IdempotencyRecordStates } from "./states.js";

export type IdempotencyRecordState =
  (typeof IdempotencyRecordStates)[keyof typeof IdempotencyRecordStates];

/** Server-derived scope and client-supplied logical-operation key. */
export interface IdempotencyRequestScope {
  readonly namespace: string;
  readonly operationId: string;
  readonly principalId: string;
  readonly clientKey: string;
  readonly tenantId?: string | null;
}

export interface QueryParameter {
  readonly name: string;
  readonly value: string;
}

/**
 * The original body text (not a parsed JS object) avoids precision loss for
 * large integers and decimal values.
 */
export interface RequestFingerprintInput {
  readonly method: string;
  readonly operationId: string;
  readonly path: string;
  readonly query?: readonly QueryParameter[];
  readonly bodyJson?: string | null;
}

export interface IdempotencyRequestIdentity {
  readonly namespace: string;
  readonly operationId: string;
  readonly principalId: string;
  readonly clientKey: string;
  readonly requestFingerprint: string;
  readonly tenantId?: string;
}

export interface StoredHttpResponse {
  readonly statusCode: number;
  readonly contentType: string;
  readonly body: Uint8Array;
  readonly headers: Readonly<Record<string, string>>;
}

import { canonicalQuote, hashV1 } from "./canonical-json.js";
import type { IdempotencyRequestScope } from "./contracts.js";

const VALID_KEY = /^[A-Za-z0-9._:-]{1,255}$/;

/** Deterministic versioned scope identity, NOT a storage claim or middleware. */
export function computeScopeKey(scope: IdempotencyRequestScope): string {
  if (!scope || typeof scope !== "object") throw new Error("Scope required.");
  for (const field of ["namespace", "operationId", "principalId"] as const) {
    if (typeof scope[field] !== "string" || scope[field].trim().length === 0)
      throw new Error("Scope field required: " + field);
  }
  if (typeof scope.clientKey !== "string" || !VALID_KEY.test(scope.clientKey))
    throw new Error("Key must be 1-255 ASCII characters from A-Z a-z 0-9 . _ : -.");
  if (scope.tenantId != null && typeof scope.tenantId !== "string")
    throw new Error("Tenant ID must be string or null.");
  return hashV1("[" + [
    canonicalQuote("idempotencykit.scope.v1"),
    canonicalQuote(scope.namespace),
    canonicalQuote(scope.operationId),
    scope.tenantId == null ? "null" : canonicalQuote(scope.tenantId),
    canonicalQuote(scope.principalId),
    canonicalQuote(scope.clientKey),
  ].join(",") + "]");
}

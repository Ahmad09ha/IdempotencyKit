import { canonicalizeJson, canonicalQuote, hashV1 } from "./canonical-json.js";
import type { QueryParameter, RequestFingerprintInput } from "./contracts.js";

const cmp = (a: string, b: string): number => a < b ? -1 : a > b ? 1 : 0;

/** Request comparison hash only; handler interception and persistence come later. */
export function computeRequestFingerprint(input: RequestFingerprintInput): string {
  if (!input || typeof input !== "object") throw new Error("Request required.");
  if (typeof input.method !== "string" || !/^[A-Za-z]+$/.test(input.method))
    throw new Error("HTTP method must contain ASCII letters.");
  if (typeof input.operationId !== "string" || !input.operationId.trim())
    throw new Error("Operation ID required.");
  if (typeof input.path !== "string" || !input.path.startsWith("/") ||
      input.path.includes("?") || input.path.includes("#"))
    throw new Error("Path must start with / and omit query and fragment.");
  const query = input.query ?? [];
  if (!Array.isArray(query)) throw new Error("Query must be an array.");
  const ordered = [...query].sort((a: QueryParameter, b: QueryParameter) =>
    cmp(a.name, b.name) || cmp(a.value, b.value)
  ).map((p: QueryParameter) => "[" + canonicalQuote(p.name) + "," + canonicalQuote(p.value) + "]");
  if (input.bodyJson != null && typeof input.bodyJson !== "string")
    throw new Error("BodyJson must be JSON text or null.");
  const body = input.bodyJson == null
    ? '["absent"]'
    : '["json",' + canonicalizeJson(input.bodyJson) + "]";
  return hashV1("[" + [
    canonicalQuote("idempotencykit.request.v1"),
    canonicalQuote(input.method.toUpperCase()),
    canonicalQuote(input.operationId),
    canonicalQuote(input.path),
    "[" + ordered.join(",") + "]",
    body,
  ].join(",") + "]");
}

export { canonicalizeJson };

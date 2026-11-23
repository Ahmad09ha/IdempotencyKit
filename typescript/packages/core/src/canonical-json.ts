import { createHash } from "node:crypto";

/**
 * IK-CJSON/1 (not RFC 8785/JCS). Exact decimal-number values, UTF-16 ordered
 * object keys, ASCII-escaped JSON strings. Do not feed pre-parsed JS numbers.
 */
const MAX_BODY_BYTES = 65_536;
const MAX_DEPTH = 32;
const MAX_EXPONENT = 1_000_000n;

class ExactNumber {
  constructor(readonly source: string) {}
}

export function hashV1(preimage: string): string {
  return "v1:" + createHash("sha256").update(preimage, "utf8").digest("hex");
}

function validateUtf16(value: string): void {
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      if (i + 1 >= value.length) throw new Error("Unpaired high surrogate.");
      const low = value.charCodeAt(++i);
      if (low < 0xdc00 || low > 0xdfff) throw new Error("Unpaired high surrogate.");
    } else if (unit >= 0xdc00 && unit <= 0xdfff) {
      throw new Error("Unpaired low surrogate.");
    }
  }
}

export function canonicalQuote(value: string): string {
  if (typeof value !== "string") throw new TypeError("Expected string.");
  validateUtf16(value);
  let out = '"';
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i);
    if (unit === 34) out += '\\"';
    else if (unit === 92) out += "\\\\";
    else if (unit < 32 || unit > 126) out += "\\u" + unit.toString(16).padStart(4, "0");
    else out += value[i];
  }
  return out + '"';
}

function normalizeNumber(source: string): string {
  const match = /^(-?)(0|[1-9][0-9]*)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?$/.exec(source);
  if (!match) throw new Error("Invalid JSON number.");
  const negative = match[1] === "-";
  const fraction = match[3] ?? "";
  const exponent = BigInt(match[4] ?? "0");
  if (exponent < -MAX_EXPONENT || exponent > MAX_EXPONENT)
    throw new Error("Exponent exceeds the v1 limit.");
  let digits = ((match[2] ?? "") + fraction).replace(/^0+/, "");
  if (digits.length === 0) return "0";
  const trailing = digits.match(/0+$/)?.[0].length ?? 0;
  if (trailing) digits = digits.slice(0, -trailing);
  const scale = exponent - BigInt(fraction.length) + BigInt(trailing);
  if (scale < -MAX_EXPONENT || scale > MAX_EXPONENT)
    throw new Error("Normalized exponent exceeds the v1 limit.");
  return (negative ? "-" : "") + digits + (scale === 0n ? "" : "e" + scale.toString());
}

function rejectUnpairedEscapes(source: string): void {
  let inString = false;
  for (let i = 0; i < source.length; i++) {
    if (source[i] === '"') { inString = !inString; continue; }
    if (!inString || source[i] !== "\\") continue;
    if (source[i + 1] !== "u") { i++; continue; }
    const code = Number.parseInt(source.slice(i + 2, i + 6), 16);
    if (code >= 0xd800 && code <= 0xdbff) {
      if (source.slice(i + 6, i + 8) !== "\\u") throw new Error("Unpaired escaped high surrogate.");
      const low = Number.parseInt(source.slice(i + 8, i + 12), 16);
      if (low < 0xdc00 || low > 0xdfff) throw new Error("Unpaired escaped high surrogate.");
      i += 11;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      throw new Error("Unpaired escaped low surrogate.");
    } else i += 5;
  }
}

/** JSON.parse drops duplicate properties, so reject them in the validated source text. */
function rejectDuplicateKeys(source: string): void {
  const contexts: Array<Set<string> | null> = [];
  for (let i = 0; i < source.length;) {
    if (source[i] === '"') {
      const start = i++;
      while (i < source.length) {
        if (source[i] === "\\") i += 2;
        else if (source[i++] === '"') break;
      }
      let after = i;
      while (after < source.length && /\s/.test(source[after]!)) after++;
      if (source[after] === ":") {
        const current = contexts[contexts.length - 1];
        if (!(current instanceof Set)) throw new Error("Malformed JSON object context.");
        const key = JSON.parse(source.slice(start, i)) as string;
        if (current.has(key)) throw new Error("Duplicate JSON key.");
        current.add(key);
      }
      continue;
    }
    const ch = source[i++];
    if (ch === "{") contexts.push(new Set<string>());
    else if (ch === "[") contexts.push(null);
    else if (ch === "}" || ch === "]") contexts.pop();
  }
}

function writeCanonical(value: unknown, depth: number): string {
  if (depth > MAX_DEPTH) throw new Error("JSON nesting exceeds the v1 limit.");
  if (value instanceof ExactNumber) return normalizeNumber(value.source);
  if (value === null) return "null";
  if (value === true) return "true";
  if (value === false) return "false";
  if (typeof value === "string") return canonicalQuote(value);
  if (Array.isArray(value)) return "[" + value.map(item => writeCanonical(item, depth + 1)).join(",") + "]";
  if (typeof value === "object" && value !== null) {
    const record = value as Record<string, unknown>;
    return "{" + Object.keys(record).sort().map(key =>
      canonicalQuote(key) + ":" + writeCanonical(record[key], depth + 1)
    ).join(",") + "}";
  }
  throw new Error("Unsupported JSON type.");
}

export function canonicalizeJson(bodyJson: string): string {
  if (typeof bodyJson !== "string") throw new TypeError("BodyJson must be text.");
  validateUtf16(bodyJson);
  if (Buffer.byteLength(bodyJson, "utf8") > MAX_BODY_BYTES)
    throw new Error("JSON body exceeds the 64 KiB limit.");

  // Reviver context.source retains exact original decimal tokens, even > 2^53.
  const parsed: unknown = JSON.parse(bodyJson,
    (_key: string, value: unknown, context?: { source?: string }): unknown => {
      if (typeof value === "number") {
        if (context?.source === undefined) throw new Error("JSON.parse source context is required.");
        return new ExactNumber(context.source);
      }
      return value;
    });
  rejectUnpairedEscapes(bodyJson);
  rejectDuplicateKeys(bodyJson);
  return writeCanonical(parsed, 0);
}

import { ConduxApiError } from "@/src/api/fetcher";

// Reading a failed control-plane call: its machine-readable code and status, and a message key looked
// up from a feature's own code table.

// The control-plane's error code, when the failure carries one.
export function readErrorCode(error: unknown): string | undefined {
  return error instanceof ConduxApiError ? error.code : undefined;
}

// The HTTP status of a control-plane failure, when the failure is one.
export function readErrorStatus(error: unknown): number | undefined {
  return error instanceof ConduxApiError ? error.status : undefined;
}

// A code's message key from a feature's table. Own properties only: a code arrives from a server or a
// URL, and a plain lookup of "constructor" or "toString" returns a function, not a key.
export function lookUpCodeKey(keys: Record<string, string>, code: string | null | undefined) {
  return code != null && Object.hasOwn(keys, code) ? keys[code] : undefined;
}

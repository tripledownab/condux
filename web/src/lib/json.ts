// Generic JSON helpers, shared low in the hierarchy so features never reimplement them.

// Pretty-prints a JSON string. If the input is not valid JSON (should not happen, but never hide it),
// the raw string is returned rather than swallowed.
export function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

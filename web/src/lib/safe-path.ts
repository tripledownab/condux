// Guard against open redirects: accept only an app-internal path so a crafted `?next=` cannot bounce a
// user to another origin. A safe path starts with a single "/" and is not a protocol-relative "//host"
// (nor its backslash variant, which some browsers normalize to "//"). Returns the path when safe, else null.
export function safeInternalPath(raw: string | null | undefined): string | null {
  if (raw?.startsWith("/") && !raw.startsWith("//") && !raw.startsWith("/\\")) {
    return raw;
  }
  return null;
}

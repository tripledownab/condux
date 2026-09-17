// Guard against open redirects: accept only an app-internal path so a crafted `?next=` cannot bounce a
// user to another origin. A safe path starts with a single "/" and is not a protocol-relative "//host"
// (nor its backslash variant, which some browsers normalize to "//"). Returns the path when safe, else null.
//
// CONTROL CHARACTERS ARE REJECTED BEFORE ANY PREFIX TEST, and that ordering is the whole point. The URL
// parser REMOVES tab, line feed and carriage return from anywhere in the input before resolving it, so
// "/<tab>//evil.com" passes every prefix test on the raw string and still reaches the browser as
// "//evil.com", which is another origin. A prefix test on the raw string tests a string the browser
// never sees, and adding more prefixes below cannot fix that: the defect is the order, not the coverage.
//
// Rejected outright rather than stripped and re-tested, because no legitimate internal path carries one
// and "what exactly do we hand back" then needs no answer. The whole C0 range plus DEL, not only the
// three the parser strips, since a narrower rule invites the same "add one more" patch next time.

// A code-point test rather than a regex: a regex spelling of this range is what the linter's
// noControlCharactersInRegex rule exists to catch, and it is right to, since a control character inside
// a pattern is nearly always a mistake. Suppressing the rule would trade a standing exception for one
// line. This needs none.
function hasControlCharacter(value: string): boolean {
  for (const character of value) {
    const code = character.codePointAt(0) ?? 0;
    if (code <= 0x1f || code === 0x7f) {
      return true;
    }
  }
  return false;
}

export function safeInternalPath(raw: string | null | undefined): string | null {
  if (!raw || hasControlCharacter(raw)) {
    return null;
  }
  if (raw.startsWith("/") && !raw.startsWith("//") && !raw.startsWith("/\\")) {
    return raw;
  }
  return null;
}

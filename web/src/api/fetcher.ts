// Custom fetch mutator for the orval-generated client (src/api/generated/**). orval's fetch
// client types every response as { status, data, headers }, so the mutator returns that shape.
// It prepends the control-plane base URL (NEXT_PUBLIC_CONDUX_API_URL; relative when unset so a
// same-origin reverse proxy also works) and throws on non-2xx so TanStack Query surfaces errors.
const BASE_URL = process.env.NEXT_PUBLIC_CONDUX_API_URL ?? "";

// The absolute URL for a control-plane path, for full-page navigations that can't go through the XHR
// mutator above (e.g. the "sign in with Google" redirect, which must land the browser on the API so the
// session cookie the callback sets is first-party).
export const apiUrl = (path: string): string => `${BASE_URL}${path}`;

// Thrown on any non-2xx response so callers (and TanStack Query) can branch on the HTTP status and the
// control-plane's machine-readable error code (ErrorResponse.error, e.g. "email_taken") instead of
// parsing a string. The message keeps the status so logs stay readable.
export class ConduxApiError extends Error {
  readonly status: number;
  readonly code?: string;

  constructor(method: string, url: string, status: number, code?: string) {
    super(`Condux API ${method} ${url} failed: ${status}`);
    this.name = "ConduxApiError";
    this.status = status;
    this.code = code;
  }
}

export const conduxFetch = async <T>(url: string, options?: RequestInit): Promise<T> => {
  const response = await fetch(`${BASE_URL}${url}`, {
    ...options,
    // Auth is an opaque `condux_session` cookie (#47); send and receive it on every request. Prod is
    // same-origin (app.condux.ai serves both), so this is a no-op there; it also lets a cross-origin
    // dev setup work once the API opts into credentialed CORS.
    credentials: "include",
    headers: { "Content-Type": "application/json", ...options?.headers },
  });

  // 204 No Content (and any empty body) parses to undefined.
  const text = await response.text();
  const body = text ? JSON.parse(text) : undefined;

  if (!response.ok) {
    const code = typeof body?.error === "string" ? body.error : undefined;
    throw new ConduxApiError(options?.method ?? "GET", url, response.status, code);
  }

  return { status: response.status, data: body, headers: response.headers } as T;
};

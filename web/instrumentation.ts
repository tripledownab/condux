// Condux on Condux (#75): the dashboard dogfoods its own SDK. @condux/nextjs wires the dashboard's
// server errors (Server Components, route handlers, server actions) to our own relay through Next's
// register + onRequestError hooks. Active only when CONDUX_DSN is set (a server runtime env, never
// NEXT_PUBLIC, so the DSN stays out of the client bundle); captureRequestError is a safe no-op otherwise.
export { captureRequestError as onRequestError, register } from "@condux/nextjs";

/**
 * Condux SDK for browsers: report errors to a Condux relay.
 *
 * Builds on the isomorphic @condux/core engine (same Sentry "store" wire shape + resilient, never-throwing
 * transport) and adds automatic capture of uncaught errors and unhandled promise rejections via the global
 * `error` / `unhandledrejection` events. Those are reported as unhandled, driving Condux's unhandled badge.
 */

export {
  addBreadcrumb,
  captureException,
  captureMessage,
  clearScope,
  sendEvent,
  setContext,
  setTag,
  setUser,
  Level,
  normalizeFramePath,
} from "@condux/core";
export type {
  Breadcrumb,
  ConduxOptions,
  ConduxUser,
  FetchLike,
  FetchResponse,
  SendResult,
} from "@condux/core";

import { type ConduxOptions, type SendResult, captureException, init as coreInit } from "@condux/core";

/** captureException from the core, with the optional `handled` flag the global handlers set to false. */
type CaptureException = (error: unknown, handled?: boolean) => Promise<SendResult>;

/** An error-like global event (a browser ErrorEvent or PromiseRejectionEvent), narrowed to what we read. */
interface ErrorLikeEvent {
  error?: unknown;
  message?: string;
  reason?: unknown;
}

/** The slice of the global object the handlers use, so tests can supply a fake instead of `window`. */
export interface GlobalErrorTarget {
  addEventListener(type: string, listener: (event: ErrorLikeEvent) => void): void;
}

/** Browser init options: the core options plus an opt-out for the automatic global error handlers. */
export type BrowserOptions = ConduxOptions & { captureGlobalErrors?: boolean };

/**
 * Wire the global `error` and `unhandledrejection` events to captureException, reported as **unhandled**
 * (they escaped the application). Fire-and-forget: a delivery never blocks or throws into the page.
 */
export function installErrorHandlers(target: GlobalErrorTarget, capture: CaptureException): void {
  target.addEventListener("error", (event) => {
    void capture(event.error ?? event.message ?? "Unknown error", false);
  });
  target.addEventListener("unhandledrejection", (event) => {
    void capture(event.reason ?? "Unhandled promise rejection", false);
  });
}

/**
 * Initialize the SDK and, unless `captureGlobalErrors` is false, install the global error handlers so
 * uncaught errors and unhandled rejections auto-report. Safe outside a browser (the handlers are only
 * installed when the global object exposes addEventListener).
 */
export function init(options: BrowserOptions): void {
  coreInit(options);
  const target = globalThis as Partial<GlobalErrorTarget>;
  if (options.captureGlobalErrors !== false && typeof target.addEventListener === "function") {
    installErrorHandlers(target as GlobalErrorTarget, captureException);
  }
}

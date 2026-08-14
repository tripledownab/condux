/**
 * Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
 * leading up to an error. Set once (or as the app's state changes) and every subsequent event carries
 * it — the first triage questions ("which customer, which plan, what did they do last") answered
 * without threading anything through capture calls. The relay already scrubs all of these at ingest
 * and derives the pseudonymous users-affected key from the user fields.
 */

import type { Level } from "./types.ts";

/** The signed-in user; the relay hashes the strongest identifier and redacts the raw email. */
export interface ConduxUser {
  id?: string;
  email?: string;
  username?: string;
}

/** One step of the trail: a navigation, a click, a request — whatever helps replay the path. */
export interface Breadcrumb {
  message: string;
  category?: string;
  level?: Level;
  type?: string;
  /** Epoch seconds; stamped automatically when omitted. */
  timestamp?: number;
  data?: Record<string, unknown>;
}

// Newest trail wins: a long-lived tab drops the oldest crumbs rather than growing without bound.
const MAX_BREADCRUMBS = 30;

let user: ConduxUser | undefined;
let tags: Record<string, string> = {};
let contexts: Record<string, Record<string, unknown>> = {};
let breadcrumbs: Breadcrumb[] = [];

/** Attach the signed-in user to subsequent events; null clears (e.g. on logout). */
export function setUser(next: ConduxUser | null): void {
  user = next ?? undefined;
}

/** Attach a tag to subsequent events; null removes it. */
export function setTag(key: string, value: string | null): void {
  if (value === null) {
    delete tags[key];
  } else {
    tags[key] = value;
  }
}

/** Attach a named context object to subsequent events; null removes it. */
export function setContext(name: string, context: Record<string, unknown> | null): void {
  if (context === null) {
    delete contexts[name];
  } else {
    contexts[name] = context;
  }
}

/** Record a breadcrumb; the trail (newest last, capped) rides every subsequent event. */
export function addBreadcrumb(crumb: Breadcrumb): void {
  breadcrumbs.push(crumb.timestamp === undefined ? { ...crumb, timestamp: Date.now() / 1000 } : crumb);
  if (breadcrumbs.length > MAX_BREADCRUMBS) {
    breadcrumbs = breadcrumbs.slice(-MAX_BREADCRUMBS);
  }
}

/** Reset all ambient state (tests, or a full sign-out). */
export function clearScope(): void {
  user = undefined;
  tags = {};
  contexts = {};
  breadcrumbs = [];
}

/** The scope's contribution to an event, holding only the keys that are actually set so an
 * unenriched event keeps its exact wire shape. Breadcrumbs use the Sentry `{values: []}` envelope. */
export function scopeFields(): {
  user?: ConduxUser;
  tags?: Record<string, string>;
  contexts?: Record<string, Record<string, unknown>>;
  breadcrumbs?: { values: Breadcrumb[] };
} {
  return {
    ...(user !== undefined ? { user } : {}),
    ...(Object.keys(tags).length > 0 ? { tags: { ...tags } } : {}),
    ...(Object.keys(contexts).length > 0 ? { contexts: { ...contexts } } : {}),
    ...(breadcrumbs.length > 0 ? { breadcrumbs: { values: [...breadcrumbs] } } : {}),
  };
}

"use client";
/**
 * The client-only React surface, on its own export path (like ./config, for the mirrored reason: a
 * class component must never reach the server/edge bundles that import the main entry from
 * instrumentation.ts). React RENDER errors go to error boundaries, not window.onerror, so wrap your
 * app in this boundary to cover the commonest class of client error:
 *
 *   import { ConduxErrorBoundary } from "@condux/nextjs/react";
 */

export { ConduxErrorBoundary } from "@condux/react";

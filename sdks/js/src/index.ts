/**
 * Condux SDK for Node.js: report errors to a Condux relay.
 *
 * A thin re-export of the isomorphic @condux/core engine under the Node package name. The core is
 * fetch / crypto / URL only (no Node built-ins), so it runs unchanged here; this package exists so a
 * Node developer installs the runtime-named SDK. Emits the Sentry "store" wire shape, so swapping the
 * DSN into a stock Sentry SDK setup works too.
 */

export * from "@condux/core";

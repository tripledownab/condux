/**
 * Condux SDK for Node.js: report errors to a Condux relay.
 *
 * Almost a re-export of the isomorphic @condux/core engine under the Node package name. The core is
 * fetch / crypto / URL only (no Node built-ins), so it runs unchanged here; this package exists so a
 * Node developer installs the runtime-named SDK. Emits the Sentry "store" wire shape, so swapping the
 * DSN into a stock Sentry SDK setup works too.
 *
 * The one thing it adds is the runtime dependency inventory (ADR-0041), because that is the one thing
 * a Node process can do and a browser cannot: read what is actually installed beside it.
 */

import { type ConduxOptions, init as initCore, setModules } from "@condux/core";
import { collectModules } from "./modules.ts";

export * from "@condux/core";
export { collectModules } from "./modules.ts";

export interface NodeOptions extends ConduxOptions {
  /**
   * Report the installed package versions with each event, so a security advisory can be answered
   * with the version actually running rather than the one a lockfile declares. On by default.
   *
   * <p>Turn it off if the payload cost matters more than the answer. The inventory is collected once
   * at init and then repeated on every event, which is what makes it survive a dropped or
   * rate-limited event, and also what makes it cost bytes on all of them.</p>
   */
  sendModules?: boolean;
}

/**
 * Initialize reporting. Identical to the core's `init` except that it also reads the installed
 * package tree once, unless `sendModules` is false.
 *
 * <p>Collection happens here rather than on first capture so the cost lands during startup, where an
 * application expects work, instead of inside the handling of its first error.</p>
 */
export function init(options: NodeOptions): void {
  initCore(options);
  if (options.sendModules !== false) {
    setModules(collectModules());
  }
}

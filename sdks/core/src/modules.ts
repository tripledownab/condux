/**
 * The runtime dependency inventory that rides events: which package versions are actually loaded, as
 * opposed to which ones a manifest declares. The relay indexes this so a CVE advisory can be answered
 * with "and you are running 4.17.11 in production" instead of only "your lockfile says so".
 *
 * Collection is NOT here, deliberately. Enumerating installed packages needs a filesystem, and this
 * package is the isomorphic core: fetch, crypto and URL only, so it runs unchanged in a browser, an
 * edge worker and Node. @condux/node does the collecting and hands the result to `setModules`. A
 * browser or edge bundle has no installed tree to enumerate and sends nothing, which reads as unknown
 * on the server rather than as "no vulnerable version present".
 *
 * Kept apart from the ambient scope even though both ride events, because `clearScope()` means
 * "forget who the user is", and forgetting which packages are loaded at the same moment would be
 * surprising: they are a fact about the process, not about the session.
 */

/**
 * The most package entries carried on one event. A large Node tree runs past this, and the cap
 * applies after sorting, so which entries survive is stable across events rather than varying with
 * directory order: the server sees one consistent set instead of a shifting sample.
 */
export const MAX_MODULES = 1000;

/**
 * How long to wait before repeating the inventory on another event.
 *
 * <p><b>This is why the feature is affordable.</b> Measured on a real Next.js application, the
 * inventory is 451 packages and about 11KB of JSON, and a large monorepo root is double that.
 * Attaching it to every event, which is what the other SDKs in this space do, would add that to every
 * single error a busy project reports, for no gain: the server deduplicates a release's inventory
 * down to one row per package per day, so all but the first event's copy is discarded on arrival.</p>
 *
 * <p>Repeating on an interval rather than sending once keeps the robustness that the every-event
 * approach buys. The event carrying the inventory can be dropped by a rate limit or a quota rejection
 * before anything parses it, so a single attempt per process would lose that day's inventory
 * outright. At this interval a process gets many attempts a day and the server needs one.</p>
 */
export const MODULES_INTERVAL_MS = 15 * 60 * 1000;

let modules: Record<string, string> | undefined;
let lastAttachedAt: number | undefined;

/**
 * Declare the loaded package versions for subsequent events; null clears them. Sorted and capped
 * here so every caller gets the same treatment, and so a caller cannot accidentally make each event
 * carry a differently ordered map.
 */
export function setModules(next: Record<string, string> | null): void {
  // A fresh declaration is news, so let the next event carry it rather than waiting out an interval
  // started by the previous inventory.
  lastAttachedAt = undefined;

  if (next === null) {
    modules = undefined;
    return;
  }

  const entries = Object.entries(next)
    .filter(([name, version]) => name.length > 0 && typeof version === "string" && version.length > 0)
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
    .slice(0, MAX_MODULES);
  modules = entries.length > 0 ? Object.fromEntries(entries) : undefined;
}

/**
 * The inventory's contribution to an event: the full map on the first event and then at most once per
 * {@link MODULES_INTERVAL_MS}, and nothing at all otherwise. Absent entirely when nothing was
 * declared, so an event from a browser keeps its exact previous wire shape.
 */
export function modulesField(now: number = Date.now()): { modules?: Record<string, string> } {
  if (modules === undefined) {
    return {};
  }
  if (lastAttachedAt !== undefined && now - lastAttachedAt < MODULES_INTERVAL_MS) {
    return {};
  }

  lastAttachedAt = now;
  return { modules: { ...modules } };
}

/** Reset the inventory and its interval. Tests only; a process has one installed tree. */
export function clearModules(): void {
  modules = undefined;
  lastAttachedAt = undefined;
}

import type { FetchLike } from "../src/index.ts";

/** A fetch that records every request body and reports success, so a test inspects exactly what the
 * SDK put on the wire — the coverage a fake transport cannot give. One copy for every suite. */
export function recordingFetch() {
  const sent: { url: string; body: string }[] = [];
  const fetch: FetchLike = async (url, init) => {
    sent.push({ url, body: init.body });
    return { status: 202, headers: { get: () => null } };
  };
  return {
    fetch,
    last: () => JSON.parse(sent[sent.length - 1].body),
    // Every event, for the assertions that are about what differs BETWEEN events rather than what one
    // of them contains.
    all: () => sent.map((request) => JSON.parse(request.body)),
  };
}

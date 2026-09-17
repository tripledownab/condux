// The ad-blocker tunnel route (ADR-0028 slice 5) and the bounded read behind it. Its own file because
// reading a stranger's request stream safely is more than the wiring the rest of the package is.

import { parseDsn } from "@condux/core";

// Events are small JSON; anything past this is not one of ours and is refused before it is forwarded.
const TUNNEL_MAX_BODY_BYTES = 1024 * 1024;

/**
 * The ad-blocker tunnel (ADR-0028 slice 5): a same-origin route that forwards browser events to the
 * relay, so an extension that cuts third-party monitoring hosts cannot drop reports. Pair it with the
 * client's `tunnel` option:
 *
 * ```ts
 * // app/monitoring/route.ts
 * import { conduxTunnelRoute } from "@condux/nextjs";
 * export const POST = conduxTunnelRoute();
 *
 * // instrumentation-client.ts
 * initClient({ tunnel: "/monitoring" });
 * ```
 *
 * The route authenticates with its own DSN (`CONDUX_DSN`, falling back to the public one), never with
 * anything the browser sent, so it can only ever report into this app's project, and abusing it is
 * exactly as possible as using the public DSN directly. The read is bounded; the relay's own rate limit
 * and quota still apply behind it.
 */
export function conduxTunnelRoute(
  options: { dsn?: string; fetch?: typeof fetch } = {},
): (request: Request) => Promise<Response> {
  const doFetch = options.fetch ?? fetch;
  return async (request: Request): Promise<Response> => {
    const dsn = options.dsn ?? process.env.CONDUX_DSN ?? process.env.NEXT_PUBLIC_CONDUX_DSN;
    if (!dsn) {
      return Response.json({ error: "tunnel_not_configured" }, { status: 503 });
    }

    const body = await readBounded(request, TUNNEL_MAX_BODY_BYTES);
    if (body === null) {
      return new Response(null, { status: 413 });
    }

    const { endpoint, projectId, publicKey } = parseDsn(dsn);
    const relayResponse = await doFetch(`${endpoint}/api/${projectId}/store/`, {
      method: "POST",
      headers: { "content-type": "application/json", "x-condux-auth": publicKey },
      body,
    });
    // Status passthrough: the browser SDK's transport reads it to decide retries (429/5xx), so hiding a
    // relay refusal here would turn every failure into a silent success.
    return new Response(await relayResponse.text(), { status: relayResponse.status });
  };
}

// Bounds the read rather than the result, and counts bytes rather than characters. Both halves matter
// and neither is what `await request.text()` does: it drains the whole stream first, so the cap only
// ever rejects what the process has already allocated, and a chunked body never has to end; and a
// string's length is UTF-16 code units, so a limit compared against it admits up to three times as
// many bytes. This route is public and unauthenticated, so the caller chooses both.
//
// Deliberately no content-length shortcut. It would refuse a declared oversize before the first read,
// which sounds better and is worth nothing here: the loop below already stops at one chunk, a streaming
// body declares no length at all, and a second way to say no is a second thing to keep true.
async function readBounded(request: Request, limit: number): Promise<ArrayBuffer | null> {
  if (!request.body) {
    return new ArrayBuffer(0);
  }

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    total += value.byteLength;
    if (total > limit) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }

  const body = new Uint8Array(total);
  let at = 0;
  for (const chunk of chunks) {
    body.set(chunk, at);
    at += chunk.byteLength;
  }
  // The buffer rather than the view: it is exactly `total` bytes because it was allocated here, and it
  // is what fetch's own body type accepts without a cast.
  return body.buffer;
}

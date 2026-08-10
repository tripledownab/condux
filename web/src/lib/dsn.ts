// DSN helpers, shared low in the hierarchy (settings today, likely SDK/docs surfaces later).

// `||` (not `??`) so an empty build arg falls back to the dev relay instead of `new URL("")` throwing.
const INGEST_URL = process.env.NEXT_PUBLIC_CONDUX_INGEST_URL || "http://localhost:9010";

// Reconstructs a key's DSN (scheme://publicKey@host/<project-uuid>), matching the control-plane's
// format, so an existing key's DSN can be shown without re-minting it. The last segment is the
// project's public UUID (#126), never the bigint. `ingestUrl` is injectable for tests.
export function buildDsn(
  publicKey: string,
  projectPublicId: string,
  ingestUrl: string = INGEST_URL,
): string {
  const url = new URL(ingestUrl);
  return `${url.protocol}//${publicKey}@${url.host}/${projectPublicId}`;
}

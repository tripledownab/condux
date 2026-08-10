/** Parse a Condux/Sentry DSN into the relay endpoint, project id, and public key. */

interface ParsedDsn {
  endpoint: string;
  projectId: string;
  publicKey: string;
}

export function parseDsn(dsn: string): ParsedDsn {
  const url = new URL(dsn); // https://<publicKey>@<host>/<projectId>
  return {
    endpoint: `${url.protocol}//${url.host}`,
    projectId: url.pathname.replace(/^\//, ""),
    publicKey: url.username,
  };
}

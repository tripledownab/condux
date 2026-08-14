// Where the hosted service's legal documents live, and whether there are any.
//
// Deliberately opt-in and unset by default, which is the point rather than an oversight. On
// app.condux.ai this points at the marketing site, and signing up means accepting those terms. On a
// self-hosted install there are no terms of ours to accept: the operator runs the software under the
// FSL, and clause 3 of our own terms says so explicitly. Baking condux.ai in would make a self-hoster's
// signup page tell their users they had agreed to a contract with us, which is simply false.
//
// A self-hoster who does have their own terms sets this to their own site and gets the same notice
// pointing at their documents.
//
// NEXT_PUBLIC_* is baked at BUILD time, so this is a Docker build arg in web/Dockerfile and in each
// compose overlay, never a runtime env. A runtime value arrives too late and silently reads as unset.

/** The documents the app links to. Values are the paths the marketing site serves them at. */
export enum LegalDocument {
  Terms = "/terms",
  Privacy = "/privacy",
}

// Read inside the function rather than at module scope so a test can stub the environment without
// resetting modules. Next inlines the literal `process.env.NEXT_PUBLIC_*` expression wherever it
// appears, so this is still baked at build exactly as it would be at the top of the file.
//
// `||` (not `??`) because CI passes the arg through even when unset, so it arrives as an empty string
// rather than undefined, and `??` would keep the empty value and produce links to "/terms" on the app
// origin, which does not exist. Same trap the landing's site.ts documents.
function configuredBaseUrl(): string {
  return process.env.NEXT_PUBLIC_CONDUX_LEGAL_URL || "";
}

/** Absolute URL of a legal document, or null when this deployment publishes none. */
export function legalUrl(
  document: LegalDocument,
  baseUrl: string = configuredBaseUrl(),
): string | null {
  if (!baseUrl) {
    return null;
  }
  // Trailing slashes are easy to leave on an env var and would otherwise produce "//terms".
  return `${baseUrl.replace(/\/+$/, "")}${document}`;
}

// Reading and dropping query parameters a flow left on the current URL, for a page that comes back from a
// third party (GitHub, Linear) with the outcome in its query string.
//
// Read from window rather than useSearchParams, which would force a Suspense boundary on every page that
// renders the panel reading them (the same reason as the auth pages).

export function readSearchParam(name: string): string | null {
  return new URLSearchParams(window.location.search).get(name);
}

// Drop parameters once they have been acted on, so a reload does not replay a finished outcome.
export function removeSearchParams(...names: string[]): void {
  const url = new URL(window.location.href);
  for (const name of names) {
    url.searchParams.delete(name);
  }
  window.history.replaceState(null, "", url.toString());
}

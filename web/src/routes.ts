// Central route paths. Redirects and links reference these instead of scattering string literals, so
// a path changes in one place. Extend as pages land.
export const ROUTES = {
  home: "/",
  login: "/login",
  signup: "/signup",
  fixes: "/fixes",
  projects: "/projects",
  developer: "/developer",
  settings: "/settings",
  // The plan + allowance live on the General tab, so anything pointing a user at their plan (an
  // exhausted fix allowance, an upgrade prompt) links here rather than to the bare /settings redirect.
  settingsGeneral: "/settings/general",
  onboarding: "/onboarding",
  admin: "/admin",
} as const;

// Issue detail is a dynamic route; build the path from an issue's public id (a UUID).
export function issueDetailPath(issueId: string): string {
  return `/issues/${issueId}`;
}

// Fix detail is a dynamic route under the Fixes surface; the id is a fix's UUID.
export function fixDetailPath(fixId: string): string {
  return `/fixes/${fixId}`;
}

// Project detail is a dynamic route under the Projects section; the id is a project's public UUID
// (the bigint id stays internal, never in a URL — #125).
export function projectDetailPath(publicId: string): string {
  return `/projects/${publicId}`;
}

// Admin org detail is a dynamic route under the admin console; the id is the org's numeric id (the admin
// console is cross-tenant and operator-only, so the internal id is fine here — ADR-0027).
export function adminOrgDetailPath(orgId: number): string {
  return `/admin/organizations/${orgId}`;
}

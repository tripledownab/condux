import { expect, type Page } from "@playwright/test";

export const ADMIN_EMAIL = process.env.CONDUX_E2E_ADMIN_EMAIL ?? "admin@condux.dev";
export const ADMIN_PASSWORD = process.env.CONDUX_E2E_ADMIN_PASSWORD ?? "condux-dev-admin";

/**
 * Signs in as the seeded dev admin and waits for the app shell.
 *
 * Shared because both specs need it and because the two-factor check below has to live in one place:
 * duplicating a guard is how the two copies drift into disagreeing about what "signed in" means.
 */
export async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(ADMIN_EMAIL);
  await page.getByLabel("Password").fill(ADMIN_PASSWORD);
  // exact:true — "Sign in with SSO" also matches a loose name.
  await page.getByRole("button", { name: "Sign in", exact: true }).click();

  const shell = page.getByRole("button", { name: /Account menu/ });
  const challenge = page.getByRole("heading", { name: "Two-factor authentication" });

  // Race the two outcomes rather than probing for one. isVisible() resolves immediately, so checking
  // the challenge first just reads "false" before it has rendered and falls through to a timeout on the
  // shell — a guard that cannot fire. or() waits for whichever appears.
  await expect(shell.or(challenge)).toBeVisible();

  // A second factor leaves the suite stuck: it cannot compute a TOTP code, because the secret is sealed
  // in the database. Say so, rather than dying on "Account menu not found" and sending the next person
  // to look at the shell.
  if (await challenge.isVisible()) {
    throw new Error(
      `${ADMIN_EMAIL} has two-factor authentication enabled, so this suite cannot sign in: it has no ` +
        "way to produce a code. Either disable MFA for that account, or point the suite at one without " +
        "it via CONDUX_E2E_ADMIN_EMAIL / CONDUX_E2E_ADMIN_PASSWORD.",
    );
  }

  // The issues surface has had no "Issues" heading since the quad-pane redesign, so the shell is what
  // "signed in" actually means.
  await expect(shell).toBeVisible();
}

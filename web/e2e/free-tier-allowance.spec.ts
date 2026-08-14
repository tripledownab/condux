import { expect, test } from "@playwright/test";

// The two Free-tier behaviours that unit tests cover in halves but nothing covered together, which is
// exactly how the bug they now guard survived: a client-side tier mirror hid the AI-fix section from
// Free orgs entirely, while the backend enforced an allowance the user could not see. The component test
// passed because it never mocked the usage query, and the API test never covered Free — each half was
// green and the pair was broken.
//
// Requires the full compose stack and the seeded dev admin, whose personal org is Free.
// Run with `pnpm e2e` (local only; not in CI).

const ADMIN_EMAIL = process.env.CONDUX_E2E_ADMIN_EMAIL ?? "admin@condux.dev";
const ADMIN_PASSWORD = process.env.CONDUX_E2E_ADMIN_PASSWORD ?? "condux-dev-admin";

async function signIn(page: import("@playwright/test").Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(ADMIN_EMAIL);
  await page.getByLabel("Password").fill(ADMIN_PASSWORD);
  // exact:true — "Sign in with SSO" also matches a loose name, and the issues surface has no "Issues"
  // heading since the quad-pane redesign, so assert on the shell instead of a heading that no longer
  // exists. The account menu only renders once a session resolves, which is what we actually mean by
  // "signed in".
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("button", { name: /Account menu/ })).toBeVisible();
}

test("a Free org can see its AI-fix allowance and compute ceiling", async ({ page }) => {
  await signIn(page);
  await page.goto("/settings/general");

  // The section itself is the regression: it was gated on a hardcoded `tier >= 1`, so a Free org saw
  // nothing at all — no allowance, no ceiling, no indication a limit even existed.
  await expect(page.getByText("AI fix compute")).toBeVisible();

  // The numbers come from the server's usage meter, so this also proves the endpoint answers for Free
  // rather than treating it as a tier without fixes.
  await expect(page.getByText(/fixes left this month/i)).toBeVisible();
  await expect(page.getByText(/Spent this month/i)).toBeVisible();

  // Free is platform-billed, not BYO, so the ceiling is read-only: no editable budget input.
  await expect(page.getByLabel(/Budget \(USD\)/)).toHaveCount(0);
});

test("a Free org is refused auto-fix mode with a reason, not a generic failure", async ({
  page,
}) => {
  await signIn(page);

  // This assertion only means anything on a Free org: a paid tier is ALLOWED to enable auto, so the
  // test would "pass" by finding no error and prove nothing. Skip loudly rather than silently invert.
  // The sibling test above needs no such guard — the compute section now renders for every tier, which
  // is the whole point of the fix it guards.
  const plan = await page.getByRole("link", { name: /^Plan/ }).innerText();
  test.skip(
    !/free/i.test(plan),
    `needs a Free org; the signed-in org is on "${plan.replace(/\s+/g, " ").trim()}"`,
  );

  await page.goto("/settings/general");

  // Free includes Conductor runs but not auto mode. The refusal must name that, since "your plan has no
  // AI fixes" stopped being true the moment Free got an allowance.
  await page.getByRole("radio", { name: /Automatic/i }).click();

  // Scoped to main: Next renders a permanently-empty route-announcer with role="alert" on every page,
  // so an unscoped getByRole("alert") is always ambiguous.
  const alert = page.getByRole("main").getByRole("alert");
  await expect(alert).toBeVisible();
  await expect(alert).toHaveText(/automatic/i);
  await expect(alert).not.toHaveText(/try again/i);
});

import { expect, test } from "@playwright/test";

// The core vertical slice: sign in, make sure a project exists, ingest an event straight into the
// relay, and confirm it surfaces as a grouped issue in the dashboard. Requires the full compose stack
// (web, control-plane, relay, consumer, stores) and the seeded dev admin. Run with `pnpm e2e` after
// `docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --build`.

const RELAY_URL = process.env.CONDUX_E2E_RELAY_URL ?? "http://localhost:9010";
const ADMIN_EMAIL = process.env.CONDUX_E2E_ADMIN_EMAIL ?? "admin@condux.dev";
const ADMIN_PASSWORD = process.env.CONDUX_E2E_ADMIN_PASSWORD ?? "condux-dev-admin";

// Splits a DSN (scheme://publicKey@host/projectId) into the pieces a relay call needs.
function parseDsn(dsn: string): { publicKey: string; projectId: string } {
  const url = new URL(dsn);
  return { publicKey: url.username, projectId: url.pathname.replace(/^\//, "") };
}

test("an ingested event surfaces as an issue in the dashboard", async ({ page, request }) => {
  // 1. Sign in as the seeded dev admin and land on the issue list.
  await page.goto("/login");
  await page.getByLabel("Email").fill(ADMIN_EMAIL);
  await page.getByLabel("Password").fill(ADMIN_PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Issues" })).toBeVisible();

  // 2. Ensure a project exists, then read its DSN from the settings page.
  await page.goto("/settings");
  const createProject = page.getByRole("button", { name: "Create project" });
  if (await createProject.isVisible()) {
    await page.getByLabel("Project name").fill("e2e");
    await createProject.click();
  }
  const dsn = await page.locator("code").first().innerText();
  const { publicKey, projectId } = parseDsn(dsn);

  // 3. Ingest an event directly into the relay (a unique title = a new grouped issue each run).
  const title = `E2E error ${Date.now()}`;
  const ingest = await request.post(`${RELAY_URL}/api/${projectId}/store/`, {
    headers: { "x-condux-auth": publicKey },
    data: { message: title, level: "error" },
  });
  expect(ingest.status()).toBe(202);

  // 4. The consumer groups it asynchronously; poll the issue list until it appears.
  await expect(async () => {
    await page.goto("/");
    await expect(page.getByText(title)).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 30_000 });
});

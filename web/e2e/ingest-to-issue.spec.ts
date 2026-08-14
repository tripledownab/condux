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
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  // The issues surface has had no "Issues" heading since the quad-pane redesign — the views are a rail
  // of buttons. Assert on the shell, which is what "signed in" actually means.
  await expect(page.getByRole("button", { name: /Account menu/ })).toBeVisible();

  // 2. Ensure a project exists, then read its DSN. Projects moved out of Settings to their own
  // top-level section (#124), and the DSN now lives behind the project's "DSN keys" tab rather than on
  // one flat settings page — this spec sat broken against the old layout because e2e only runs locally.
  await page.goto("/projects");
  const createProject = page.getByRole("button", { name: /Create project/i });
  if (await createProject.isVisible().catch(() => false)) {
    await page.getByLabel(/Project name|Name/i).fill("e2e");
    await createProject.click();
  }
  await page.locator('a[href^="/projects/"]').first().click();
  await page.getByRole("tab", { name: "DSN keys" }).click();

  // The DSN is rendered in a <code> block; waiting on it rather than reading immediately, since the key
  // list loads only once its tab is mounted.
  const dsnCode = page.locator("code").first();
  await expect(dsnCode).toContainText("@", { timeout: 15_000 });
  const { publicKey, projectId } = parseDsn(await dsnCode.innerText());

  // 3. Ingest an event directly into the relay (a unique title = a new grouped issue each run).
  const title = `E2E error ${Date.now()}`;
  const ingest = await request.post(`${RELAY_URL}/api/${projectId}/store/`, {
    headers: { "x-condux-auth": publicKey },
    data: { message: title, level: "error" },
  });
  // 200, not 202: the Sentry-compatible endpoints deliberately answer 200 because sentry-dart reads the
  // event id back only from one. Asserting the exact status is the point — a 4xx here would otherwise
  // pass silently into a polling loop that just times out with no hint of why.
  expect(ingest.status()).toBe(200);

  // 4. The consumer groups it asynchronously; poll the issue list until it appears.
  await expect(async () => {
    await page.goto("/");
    await expect(page.getByText(title)).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 30_000 });
});

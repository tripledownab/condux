import { defineConfig, devices } from "@playwright/test";

// End-to-end tests (web/e2e). They drive the real dashboard against a running compose stack, so they
// are LOCAL-ONLY (not in CI, which has no stack) - like the backend integration tests. The stack is
// started separately (docker compose ... up); Playwright does not manage it. Run: `pnpm e2e`.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: "list",
  use: {
    baseURL: process.env.CONDUX_E2E_BASE_URL ?? "http://localhost:3000",
    trace: "on-first-retry",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});

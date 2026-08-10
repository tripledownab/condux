// Marketing screenshots of the running dashboard via headless Chromium (Playwright).
// The dashboard must already be running (localhost:3000 + control-plane :8080); this script never starts it.
// Navigation waits on "load", not "networkidle": the authed shell keeps a live SSE stream open for the nav
// badges (ADR-0030), so the page never reaches network-idle. Content settles via waitForSelector + a pause.
// Captures the key surfaces in both dark and light theme at a roomy, retina resolution.
// Usage: node scripts/screenshots.mjs   (from web/)
//   env: SHOT_BASE_URL, SHOT_EMAIL, SHOT_PASSWORD, HERO_ISSUE (title substring), SHOT_PROJECT (uuid)
// For clean marketing shots, sign in as a NON platform-admin (else the operator-only "Admin" nav shows):
// pass SHOT_EMAIL/SHOT_PASSWORD for a normal org member/admin, not an account in CONDUX_PLATFORM_ADMIN_EMAILS.
import { mkdirSync } from "node:fs";
import { chromium } from "@playwright/test";

const BASE = process.env.SHOT_BASE_URL ?? "http://localhost:3000";
const EMAIL = process.env.SHOT_EMAIL ?? "admin@condux.dev";
const PASSWORD = process.env.SHOT_PASSWORD ?? "condux-dev-admin";
// The issue featured in the detail/fix shots (a real bug reads better than a benign cancellation).
const HERO = process.env.HERO_ISSUE ?? "NullReferenceException";
const PROJECT = process.env.SHOT_PROJECT ?? "019faf08-e57d-7359-9171-542956b9181b";
// The MCP connect snippet's URL comes from the dashboard's build-time API base, which is localhost in dev.
// For marketing we rewrite it to the public endpoint a real customer sees.
const PUBLIC_URL = process.env.SHOT_PUBLIC_URL ?? "https://app.condux.ai";
const OUT = "screenshots";
mkdirSync(OUT, { recursive: true });

const shot = async (page, name) => {
  await page.waitForTimeout(700); // let charts, fonts and any transition settle
  // Hide Next.js's dev-mode indicator (the corner badge) so it doesn't ride the marketing shots. Its host
  // element lives in the light DOM, so hiding the host hides its shadow content too; re-applied each shot
  // because a navigation drops the injected style.
  await page
    .addStyleTag({
      content: "nextjs-portal,[data-next-badge-root],#__next-build-watcher{display:none!important}",
    })
    .catch(() => {});
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log(`  ✓ ${OUT}/${name}.png`);
};

async function login(page) {
  await page.goto(BASE, { waitUntil: "load" });
  const email = page.getByLabel("Email", { exact: false });
  // The auth form renders client-side a beat after "load"; wait for it before deciding whether to log in
  // (times out harmlessly when already authenticated, i.e. the form never appears).
  await email.waitFor({ state: "visible", timeout: 10000 }).catch(() => {});
  if (await email.isVisible().catch(() => false)) {
    await email.fill(EMAIL);
    await page.getByLabel("Password", { exact: false }).fill(PASSWORD);
    await page.getByRole("button", { name: /sign in/i }).click();
    await page.waitForURL((url) => !/\/login|\/auth/.test(url.pathname), { timeout: 15000 });
  }
}

// Open an issue whose title contains `match`, falling back to the first issue in the list.
async function openIssue(page, match) {
  await page.goto(BASE, { waitUntil: "load" });
  const links = page.locator("a[href*='/issues/']");
  await links.first().waitFor({ state: "visible", timeout: 25000 }); // list renders a beat after load in dev
  const preferred = page.locator("a[href*='/issues/']", { hasText: match }).first();
  const target = (await preferred.count()) ? preferred : links.first();
  await target.click();
  await page.waitForTimeout(1500); // let the detail render (client-side nav, no load event)
}

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1920, height: 1200 }, // roomy so the quad-pane does not feel cramped
  deviceScaleFactor: 2, // retina-crisp for the web
});
const page = await context.newPage();

try {
  await login(page);

  for (const theme of ["dark", "light"]) {
    const sfx = theme === "dark" ? "" : "-light";
    console.log(`theme: ${theme}`);
    // next-themes reads localStorage.theme (attribute=data-theme); set it, then each goto renders themed.
    await page.evaluate((t) => localStorage.setItem("theme", t), theme);

    // 1. Issues surface (overview).
    await page.goto(BASE, { waitUntil: "load" });
    await page.waitForSelector("a[href*='/issues/']", { timeout: 15000 }).catch(() => {});
    await shot(page, `issues-list${sfx}`);

    // 2. Issue detail (the hero: code-path trace + chart + right rail).
    await openIssue(page, HERO);
    await shot(page, `issue-detail${sfx}`);

    // 3. Fix panel: open the Conductor "Suggest fix" dialog (capture only, never confirm).
    const suggest = page.getByRole("button", { name: /suggest fix/i }).first();
    if (await suggest.count()) {
      await suggest.click();
      await page.waitForTimeout(900);
      await shot(page, `fix-panel${sfx}`);
      await page.keyboard.press("Escape").catch(() => {});
    }

    // 4. MCP tab on the project detail. Mint a token first (when the create form is available, i.e. an
    // org admin) so the shot shows a freshly-added token + its connect snippet — a "token added" state
    // that actually sells the feature. Best-effort: if the form is absent (a member) it just shots the tab.
    await page.goto(`${BASE}/projects/${PROJECT}`, { waitUntil: "load" });
    const mcpTab = page.getByRole("tab", { name: /^mcp$/i }).first();
    await mcpTab.waitFor({ state: "visible", timeout: 20000 }).catch(() => {}); // tabs render after load
    if (await mcpTab.count()) {
      await mcpTab.click();
      const nameInput = page.getByRole("textbox").first();
      await nameInput.waitFor({ state: "visible", timeout: 10000 }).catch(() => {});
      const createButton = page.getByRole("button", { name: /create/i }).first();
      if ((await createButton.count()) && (await nameInput.count())) {
        await nameInput.fill("Claude (Cursor)");
        await createButton.click();
        await page.waitForTimeout(1200); // the mint + connect snippet render
      }
    }
    // Rewrite the connect snippet's dev URL (localhost) to the public endpoint for the marketing shot.
    await page
      .evaluate((publicUrl) => {
        for (const pre of document.querySelectorAll("pre")) {
          if (pre.textContent?.includes("/api/mcp")) {
            pre.textContent = pre.textContent.replace(/https?:\/\/localhost:\d+/g, publicUrl);
          }
        }
      }, PUBLIC_URL)
      .catch(() => {});
    await shot(page, `mcp-tab${sfx}`);

    // 5. A completed Conductor fix: the Fixes surface with the fix detail (a real, seeded fix run —
    // draft PR, model + token cost, the fix summary).
    await page.goto(`${BASE}/fixes`, { waitUntil: "load" });
    const fixLink = page.locator("a[href*='/fixes/']").first();
    await fixLink.waitFor({ state: "visible", timeout: 20000 }).catch(() => {});
    if (await fixLink.count()) {
      await fixLink.click();
      await page.waitForTimeout(1500); // detail renders (client-side nav)
    }
    await shot(page, `fix-run${sfx}`);
  }
} finally {
  await context.close();
  await browser.close();
}
console.log("done");

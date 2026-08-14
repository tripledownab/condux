import { initClient } from "@condux/nextjs";

// Condux on Condux (#75), client half: browser errors in the dashboard (render crashes, uncaught
// rejections) report to our own relay, and with the maps uploaded at build time (ADR-0028) they arrive
// symbolicated. The env vars are referenced HERE, not left to the SDK's own fallbacks, because Next
// inlines NEXT_PUBLIC_* where application code mentions them. Events go through the same-origin
// /monitoring tunnel (app/monitoring/route.ts), so an ad blocker cutting the ingest host cannot drop
// them; without a DSN baked (a local pnpm dev) this is a no-op.
initClient({
  dsn: process.env.NEXT_PUBLIC_CONDUX_DSN,
  release: process.env.NEXT_PUBLIC_CONDUX_RELEASE,
  tunnel: "/monitoring",
});

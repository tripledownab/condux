import { conduxTunnelRoute } from "@condux/nextjs";

// The ad-blocker tunnel (ADR-0028 slice 5): browser events from instrumentation-client.ts land here
// same-origin and are forwarded to the relay with this server's own CONDUX_DSN — inside compose that is
// the internal relay address, which the browser could never reach directly.
export const POST = conduxTunnelRoute();

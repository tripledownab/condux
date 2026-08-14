import { withConduxConfig } from "@condux/nextjs/config";
import createNextIntlPlugin from "next-intl/plugin";

/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
};

// Wires the next-intl request config (i18n/request.ts) so server and client components share the catalog.
const withNextIntl = createNextIntlPlugin();

// Condux on Condux (#75): the dashboard's own client chunks get debugIds + stripped maps (ADR-0028).
// Uploads happen only where CONDUX_URL + CONDUX_RELEASE + CONDUX_RELEASE_TOKEN are set (the deploy);
// a local or CI build just stamps the chunks.
export default withConduxConfig(withNextIntl(nextConfig));

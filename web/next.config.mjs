import createNextIntlPlugin from "next-intl/plugin";

/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
};

// Wires the next-intl request config (i18n/request.ts) so server and client components share the catalog.
const withNextIntl = createNextIntlPlugin();

export default withNextIntl(nextConfig);

import type { MetadataRoute } from "next";

// The dashboard is an authenticated application, so nothing here belongs in a search index. Without
// this Next emits no robots.txt at all and every page is fair game: the login and signup pages render
// for anonymous visitors, so they are exactly what a crawler would reach and index.
//
// The marketing site is the surface meant to rank, and it serves its own robots.txt + sitemap from
// landing/. Keeping the two apart means condux.ai competes for search traffic and app.condux.ai does
// not dilute it with near-empty auth pages.
export default function robots(): MetadataRoute.Robots {
  return { rules: { userAgent: "*", disallow: "/" } };
}

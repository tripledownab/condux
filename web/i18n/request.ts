import { getRequestConfig } from "next-intl/server";

// next-intl request config. The dashboard ships a single locale for now (English); the messages live
// in one catalog per locale (messages/<locale>.json). Adding a language later means adding a catalog
// and, if we want URL-based switching, the [locale] routing - the call sites already use keys.
const LOCALE = "en";

export default getRequestConfig(async () => ({
  locale: LOCALE,
  messages: (await import(`../messages/${LOCALE}.json`)).default,
}));

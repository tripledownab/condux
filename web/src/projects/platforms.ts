import { useTranslations } from "next-intl";

// The platforms a project can target, kept as data so the create form and the project displays stay in
// sync (single source of truth). Values are stable lowercase identifiers stored on the project; labels
// are translated under the "platforms" namespace. Ordered by our SDK coverage, then the Tier-1 languages,
// then a catch-all, since a Sentry SDK reaches platforms we ship no SDK for. A project created before
// this dropdown could hold a free-text value, so an unknown key falls back to rendering itself.
export const PLATFORM_KEYS = [
  "javascript",
  "browser",
  "python",
  "go",
  "java",
  "csharp",
  "php",
  "ruby",
  "rust",
  "other",
] as const;

export type PlatformKey = (typeof PLATFORM_KEYS)[number];

export const DEFAULT_PLATFORM: PlatformKey = "javascript";

export function isKnownPlatform(value: string): value is PlatformKey {
  return (PLATFORM_KEYS as readonly string[]).includes(value);
}

// Resolves a stored platform value to its display label, falling back to the raw value for a custom one.
export function usePlatformLabel(): (value: string) => string {
  const translate = useTranslations("platforms");
  return (value) => (isKnownPlatform(value) ? translate(value) : value);
}

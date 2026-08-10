// The theme choices offered in the UI. "system" follows the OS preference. Backed by a string enum
// (values match what next-themes persists) so call sites use Theme.Light rather than a bare literal.
export enum Theme {
  Light = "light",
  Dark = "dark",
  System = "system",
}

// The order the controls render in. Each value doubles as its label key under the "theme" namespace
// (theme.light, theme.dark, theme.system), which the toggle translates.
export const THEME_OPTIONS: readonly Theme[] = [Theme.Light, Theme.Dark, Theme.System];

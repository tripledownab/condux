// react-resizable-panels persists pane layouts through this storage. Its default reaches for the
// bare localStorage global, which does not exist during prerendering (and not in every test DOM), so
// the storage is resolved explicitly with an inert fallback.
export const layoutStorage: {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
} =
  typeof window === "undefined" || typeof window.localStorage?.getItem !== "function"
    ? { getItem: () => null, setItem: () => {} }
    : window.localStorage;

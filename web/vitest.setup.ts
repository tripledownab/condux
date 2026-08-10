import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";

// next-themes reads the OS color-scheme via matchMedia, which jsdom does not implement. Stub it so
// the theme components mount in tests (defaults to no match, i.e. light-preference).
Object.defineProperty(window, "matchMedia", {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }),
});

// Radix primitives (dropdowns, dialogs) call these DOM APIs that jsdom does not implement. Stub them
// so shadcn/ui components built on Radix can be opened and interacted with in component tests.
for (const method of [
  "scrollIntoView",
  "hasPointerCapture",
  "setPointerCapture",
  "releasePointerCapture",
]) {
  if (!(method in Element.prototype)) {
    Object.defineProperty(Element.prototype, method, { writable: true, value: vi.fn() });
  }
}

// localStorage is not functional in this jsdom environment. Back it with a real in-memory store so
// persistence code (sidebar collapse, org/project selection) runs for real in tests.
if (typeof window.localStorage?.getItem !== "function") {
  const backing = new Map<string, string>();
  Object.defineProperty(window, "localStorage", {
    writable: true,
    value: {
      getItem: (key: string) => backing.get(key) ?? null,
      setItem: (key: string, value: string) => void backing.set(key, String(value)),
      removeItem: (key: string) => void backing.delete(key),
      clear: () => backing.clear(),
      key: (index: number) => [...backing.keys()][index] ?? null,
      get length() {
        return backing.size;
      },
    },
  });
}

// react-resizable-panels measures its groups with ResizeObserver, which jsdom does not implement.
// An inert stub lets the resizable layouts mount in tests (no resize events ever fire).
if (typeof window.ResizeObserver === "undefined") {
  window.ResizeObserver = class {
    observe = vi.fn();
    unobserve = vi.fn();
    disconnect = vi.fn();
  };
}

// Unmount React trees between tests so component state does not leak across cases.
afterEach(cleanup);

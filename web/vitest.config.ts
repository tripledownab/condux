import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// Vitest runs the dashboard's unit and component tests. jsdom gives the component tests a DOM, and
// the setup file wires jest-dom matchers plus a matchMedia stub (next-themes needs it). The "@" alias
// mirrors tsconfig's paths so tests import the same way the app does.
const rootDir = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { "@": rootDir },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./vitest.setup.ts"],
    // The generated client is committed and not worth unit-testing directly; e2e/ is Playwright, not Vitest.
    exclude: ["node_modules/**", "src/api/generated/**", ".next/**", "e2e/**"],
  },
});

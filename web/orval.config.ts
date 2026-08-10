import { defineConfig } from "orval";

// Generates a typed TanStack Query client for the dashboard from the control-plane's
// OpenAPI contract (../contracts/openapi.json, exported from the .NET API). Regenerate with
// `pnpm gen:api` whenever the API contract changes. The output is committed and lint-ignored.
export default defineConfig({
  condux: {
    input: "../contracts/openapi.json",
    output: {
      mode: "split",
      target: "./src/api/generated/condux.ts",
      schemas: "./src/api/generated/model",
      client: "react-query",
      httpClient: "fetch",
      clean: true,
      override: {
        mutator: { path: "./src/api/fetcher.ts", name: "conduxFetch" },
        query: { useQuery: true },
      },
    },
  },
});

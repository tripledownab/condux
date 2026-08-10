// The built-in views of the issues surface: presets of the same mechanism user-saved views use —
// a search query (the saved kind adds its own ordering). Names live under "issues.views".
export type BuiltinView = { key: string; query: string };

export const BUILTIN_VIEWS: BuiltinView[] = [
  { key: "unresolved", query: "is:unresolved" },
  { key: "resolved", query: "is:resolved" },
  { key: "ignored", query: "is:ignored" },
  { key: "all", query: "" },
];

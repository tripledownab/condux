// Issue-domain label mappings (Condux Level / status enums -> catalog keys + colors). Generic
// formatting helpers live in @/src/lib (time, json, strings).

export type LevelMeta = { key: string; className: string };

// Condux.Core Level (Unspecified=0, Debug=1, Info=2, Warning=3, Error=4, Fatal=5) -> a label key under
// "issues.level" plus the brand event-level color. The label always travels with the color, so
// severity is never conveyed by color alone (accessibility).
const LEVEL_META: Record<number, LevelMeta> = {
  0: { key: "unknown", className: "text-debug" },
  1: { key: "debug", className: "text-debug" },
  2: { key: "info", className: "text-info" },
  3: { key: "warning", className: "text-warning" },
  4: { key: "error", className: "text-error" },
  5: { key: "fatal", className: "text-fatal" },
};

export function levelMeta(level: number): LevelMeta {
  return LEVEL_META[level] ?? { key: "unknown", className: "text-debug" };
}

// Issue status (Postgres: 1=unresolved, 2=resolved, 3=ignored) -> a label key under "issues.status".
const STATUS_KEYS: Record<number, string> = {
  1: "unresolved",
  2: "resolved",
  3: "ignored",
};

export function statusKey(status: number): string {
  // An unexpected value surfaces as "unknown" rather than masquerading as a real status.
  return STATUS_KEYS[status] ?? "unknown";
}

// The compact level glyphs of the issue rail (lab 30): fatal demands the eye, the rest stay quiet.
const LEVEL_GLYPHS: Record<number, string> = {
  5: "\u2716",
  4: "\u25cf",
  3: "\u25b2",
};

export function levelGlyph(level: number): string {
  return LEVEL_GLYPHS[level] ?? "\u25cb";
}

"use client";

import { RiCloseLine } from "@remixicon/react";
import { useTranslations } from "next-intl";
import { type FormEvent, useState } from "react";
import type { IssueCountsResponseByLevel, SavedView } from "@/src/api/generated/model";
import { levelMeta } from "./issue-format";
import { BUILTIN_VIEWS } from "./issue-views";

// Severity rows read the per-level facet counts, highest first; silent levels do not render.
const SEVERITY_LEVELS = [5, 4, 3, 2, 1];

// The first rail of the issues surface: the severity breakdown on top (it filters the current
// list), then the views — the built-in presets followed by the user's saved views (select, save
// the current search + ordering under a name, delete). The surface owns all state; counts come
// pre-computed from the server (null while they load, so a count reads blank rather than a stale 0).
export function ViewsRail({
  levelCounts,
  levelFilter,
  onLevelFilterChange,
  activeBuiltinKey,
  builtinCount,
  onSelectBuiltin,
  savedViews,
  activeSavedId,
  onSelectSaved,
  onSaveCurrent,
  onDeleteSaved,
  savePending,
}: {
  levelCounts: IssueCountsResponseByLevel | undefined;
  levelFilter: number | null;
  onLevelFilterChange: (level: number | null) => void;
  activeBuiltinKey: string | null;
  builtinCount: (query: string) => number | null;
  onSelectBuiltin: (query: string) => void;
  savedViews: SavedView[];
  activeSavedId: number | null;
  onSelectSaved: (view: SavedView) => void;
  onSaveCurrent: (name: string) => void;
  onDeleteSaved: (id: number) => void;
  savePending: boolean;
}) {
  const translate = useTranslations("issues");
  const [saveName, setSaveName] = useState("");

  const submitSave = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const trimmed = saveName.trim();
    if (trimmed.length === 0) {
      return;
    }
    onSaveCurrent(trimmed);
    setSaveName("");
  };

  return (
    <aside className="h-full overflow-y-auto bg-card/50 p-3 text-xs">
      <SeverityBreakdown
        levelCounts={levelCounts}
        levelFilter={levelFilter}
        onToggle={(level) => onLevelFilterChange(levelFilter === level ? null : level)}
      />

      <div className="mt-4 font-medium uppercase text-muted-foreground">
        {translate("views.title")}
      </div>
      <div className="mt-1.5 flex flex-col gap-0.5">
        {BUILTIN_VIEWS.map((view) => (
          <button
            key={view.key}
            type="button"
            aria-pressed={view.key === activeBuiltinKey}
            onClick={() => onSelectBuiltin(view.query)}
            className={`flex items-center justify-between rounded px-2 py-1.5 text-left ${
              view.key === activeBuiltinKey
                ? "bg-secondary text-foreground"
                : "text-muted-foreground hover:bg-secondary/50"
            }`}
          >
            <span>{translate(`views.${view.key}`)}</span>
            <span className="tabular-nums">{builtinCount(view.query) ?? ""}</span>
          </button>
        ))}
        {savedViews.map((view) => (
          <div
            key={view.id}
            className={`group flex items-center rounded ${
              view.id === activeSavedId ? "bg-secondary" : "hover:bg-secondary/50"
            }`}
          >
            <button
              type="button"
              aria-pressed={view.id === activeSavedId}
              onClick={() => onSelectSaved(view)}
              className={`min-w-0 flex-1 truncate px-2 py-1.5 text-left ${
                view.id === activeSavedId ? "text-foreground" : "text-muted-foreground"
              }`}
            >
              {view.name}
            </button>
            <button
              type="button"
              aria-label={translate("views.delete", { name: view.name })}
              onClick={() => onDeleteSaved(view.id)}
              className="hidden shrink-0 px-1.5 text-muted-foreground hover:text-error group-hover:block"
            >
              <RiCloseLine className="size-3.5" aria-hidden="true" />
            </button>
          </div>
        ))}
      </div>

      {/* Save the current search + ordering under a name; an existing name is overwritten. */}
      <form onSubmit={submitSave} className="mt-3 flex items-center gap-1">
        <input
          value={saveName}
          onChange={(event) => setSaveName(event.target.value)}
          placeholder={translate("views.savePlaceholder")}
          aria-label={translate("views.saveLabel")}
          className="min-w-0 flex-1 rounded border border-border bg-background px-2 py-1 text-xs text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        />
        <button
          type="submit"
          disabled={savePending}
          className="shrink-0 rounded bg-secondary px-2 py-1 text-foreground transition-colors hover:bg-secondary/70 disabled:opacity-60"
        >
          {translate("views.save")}
        </button>
      </form>
    </aside>
  );
}

function SeverityBreakdown({
  levelCounts,
  levelFilter,
  onToggle,
}: {
  levelCounts: IssueCountsResponseByLevel | undefined;
  levelFilter: number | null;
  onToggle: (level: number) => void;
}) {
  const translate = useTranslations("issues");
  // A level shows when it has occurrences (or is the active filter, so it stays clearable). Until the
  // counts load every level reads 0, so the section is briefly empty rather than showing wrong numbers.
  const rows = SEVERITY_LEVELS.map((level) => ({
    level,
    count: levelCounts?.[String(level)] ?? 0,
  })).filter((row) => row.count > 0 || row.level === levelFilter);
  if (rows.length === 0) {
    return null;
  }
  return (
    <>
      <div className="font-medium uppercase text-muted-foreground">{translate("severity")}</div>
      <div className="mt-1.5 flex flex-col gap-0.5">
        {rows.map((row) => (
          <button
            key={row.level}
            type="button"
            aria-pressed={row.level === levelFilter}
            onClick={() => onToggle(row.level)}
            className={`flex justify-between rounded px-2 py-1 text-left ${
              row.level === levelFilter ? "bg-secondary" : "hover:bg-secondary/50"
            }`}
          >
            <span className={levelMeta(row.level).className}>
              {translate(`level.${levelMeta(row.level).key}`)}
            </span>
            <span className="tabular-nums text-muted-foreground">{row.count}</span>
          </button>
        ))}
      </div>
    </>
  );
}

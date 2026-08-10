import { useTranslations } from "next-intl";
import { levelMeta } from "./issue-format";

// A severity badge: a colored dot plus the level label. The label is always present so severity is
// conveyed by more than color alone. Reused by the issue list and (later) the issue detail.
export function LevelBadge({ level }: { level: number }) {
  const translate = useTranslations("issues.level");
  const { key, className } = levelMeta(level);
  return (
    <span className={`inline-flex items-center gap-1.5 text-xs font-medium ${className}`}>
      <span aria-hidden className="h-2 w-2 rounded-full bg-current" />
      {translate(key)}
    </span>
  );
}

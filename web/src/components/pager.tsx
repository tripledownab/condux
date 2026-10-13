"use client";

import { useTranslations } from "next-intl";
import { toPageItems } from "@/src/lib/pagination";
import { cn } from "@/src/lib/utils";

// A numbered pager: previous, the page numbers from toPageItems with gaps, and next. Generic, so any
// paged list can use it; it renders nothing for a single page. Pages are zero-based in, one-based shown.

const BUTTON_CLASS =
  "min-w-7 rounded-md border border-border px-2 py-1 text-xs text-foreground transition-colors hover:bg-secondary disabled:opacity-50 disabled:hover:bg-transparent";

export function Pager({
  page,
  pageCount,
  onPageChange,
}: {
  page: number;
  pageCount: number;
  onPageChange: (page: number) => void;
}) {
  const translate = useTranslations("common.pager");
  if (pageCount <= 1) {
    return null;
  }

  // A gap is keyed by the page it follows, which is always a number and differs between the two gaps.
  return (
    <nav aria-label={translate("label")} className="flex items-center gap-1">
      <button
        type="button"
        className={BUTTON_CLASS}
        disabled={page === 0}
        onClick={() => onPageChange(page - 1)}
      >
        {translate("previous")}
      </button>
      {toPageItems(page, pageCount).map((item, position, items) =>
        item === "gap" ? (
          <span
            key={`gap-after-${items[position - 1]}`}
            aria-hidden
            className="px-1 text-muted-foreground"
          >
            …
          </span>
        ) : (
          <button
            key={item}
            type="button"
            aria-label={translate("page", { page: item + 1 })}
            aria-current={item === page ? "page" : undefined}
            className={cn(
              BUTTON_CLASS,
              item === page && "border-primary bg-primary text-primary-foreground hover:bg-primary",
            )}
            onClick={() => onPageChange(item)}
          >
            {item + 1}
          </button>
        ),
      )}
      <button
        type="button"
        className={BUTTON_CLASS}
        disabled={page >= pageCount - 1}
        onClick={() => onPageChange(page + 1)}
      >
        {translate("next")}
      </button>
    </nav>
  );
}

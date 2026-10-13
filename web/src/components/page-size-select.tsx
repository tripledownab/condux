"use client";

import { useTranslations } from "next-intl";
import { Combobox } from "@/src/components/ui/combobox";

// A rows-per-page picker for a paged list. Generic, so any list with a Pager can use it. Kept apart from
// Pager on purpose: Pager hides itself on a single page, and this must stay, or a list that fits on one
// large page could never be set back to a smaller one.

export function PageSizeSelect({
  pageSize,
  pageSizes,
  onPageSizeChange,
}: {
  pageSize: number;
  pageSizes: readonly number[];
  onPageSizeChange: (pageSize: number) => void;
}) {
  const translate = useTranslations("common.pager");
  const translateCommon = useTranslations("common");
  const options = pageSizes.map((size) => ({ value: String(size), label: String(size) }));

  return (
    <div className="flex items-center gap-2">
      <span>{translate("pageSize")}</span>
      <Combobox
        value={String(pageSize)}
        onValueChange={(value) => onPageSizeChange(Number(value))}
        options={options}
        aria-label={translate("pageSize")}
        searchPlaceholder={translateCommon("comboboxSearch")}
        emptyText={translateCommon("comboboxEmpty")}
        className="w-20"
      />
    </div>
  );
}

"use client";

import { RiArrowDownSLine, RiCheckLine } from "@remixicon/react";
import { useEffect, useRef, useState } from "react";
import { FIELD_CLASS } from "@/src/components/form";
import { Button } from "@/src/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/src/components/ui/popover";
import { cn } from "@/src/lib/utils";

export type ComboboxOption = { value: string; label: string };

// The options whose label matches the query (case-insensitive substring). Pure, so the filtering is unit
// testable without the client component. Exported for that test.
export function filterComboboxOptions(
  options: readonly ComboboxOption[],
  query: string,
): ComboboxOption[] {
  const needle = query.trim().toLowerCase();
  return needle === ""
    ? [...options]
    : options.filter((option) => option.label.toLowerCase().includes(needle));
}

interface ComboboxProps {
  value: string;
  onValueChange: (value: string) => void;
  options: readonly ComboboxOption[];
  // Set on the trigger so a <label htmlFor> (e.g. the shared Field) associates with it.
  id?: string;
  placeholder?: string;
  searchPlaceholder?: string;
  emptyText?: string;
  disabled?: boolean;
  className?: string;
  "aria-label"?: string;
}

// A searchable single-select (combobox): a Popover trigger showing the current label, over a search box +
// a filtered, keyboard-navigable option list. Built on Radix Popover directly (like our dialogs), so it
// carries no extra dependency. Keyboard: ↑/↓ move (wrapping), Enter selects, Esc closes; the highlight
// follows the pointer and resets when the query changes. A clean-room take on a searchable-select pattern.
export function Combobox({
  value,
  onValueChange,
  options,
  id,
  placeholder = "Select…",
  searchPlaceholder = "Search…",
  emptyText = "No results.",
  disabled = false,
  className,
  "aria-label": ariaLabel,
}: ComboboxProps) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState("");
  const [highlight, setHighlight] = useState(-1);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const filtered = filterComboboxOptions(options, search);
  const selected = options.find((option) => option.value === value);

  // Keep the highlighted row in view as the arrows move it.
  useEffect(() => {
    if (highlight < 0 || listRef.current === null) {
      return;
    }
    const rows = listRef.current.querySelectorAll("[data-option]");
    rows[highlight]?.scrollIntoView({ block: "nearest" });
  }, [highlight]);

  const changeOpen = (next: boolean) => {
    setOpen(next);
    if (!next) {
      setSearch("");
      setHighlight(-1);
    }
  };

  const select = (optionValue: string) => {
    onValueChange(optionValue);
    changeOpen(false);
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === "ArrowDown" && filtered.length > 0) {
      event.preventDefault();
      setHighlight((current) => (current + 1) % filtered.length);
    } else if (event.key === "ArrowUp" && filtered.length > 0) {
      event.preventDefault();
      setHighlight((current) => (current - 1 + filtered.length) % filtered.length);
    } else if (event.key === "Enter") {
      event.preventDefault();
      const option = filtered[highlight];
      if (option) {
        select(option.value);
      }
    } else if (event.key === "Escape") {
      event.preventDefault();
      changeOpen(false);
    }
  };

  return (
    <Popover open={open} onOpenChange={changeOpen}>
      <PopoverTrigger asChild>
        <Button
          id={id}
          type="button"
          variant="outline"
          role="combobox"
          aria-expanded={open}
          aria-label={ariaLabel}
          disabled={disabled}
          className={cn(
            "w-full justify-between font-normal",
            !selected && "text-muted-foreground",
            className,
          )}
        >
          <span className="truncate">{selected ? selected.label : placeholder}</span>
          <RiArrowDownSLine className="ml-2 size-4 shrink-0 opacity-50" aria-hidden="true" />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        align="start"
        onOpenAutoFocus={(event) => {
          event.preventDefault();
          inputRef.current?.focus();
        }}
        className="w-[var(--radix-popover-trigger-width)] gap-0 overflow-hidden p-0"
      >
        <div className="p-2">
          <input
            ref={inputRef}
            value={search}
            onChange={(event) => {
              // A stale highlight could select the wrong row after the list narrows; reset it as the
              // query changes so the arrow keys re-navigate against the current list.
              setSearch(event.target.value);
              setHighlight(-1);
            }}
            onKeyDown={onKeyDown}
            placeholder={searchPlaceholder}
            aria-label={searchPlaceholder}
            className={cn(FIELD_CLASS, "h-9 w-full")}
          />
        </div>
        <div ref={listRef} role="listbox" className="max-h-60 overflow-y-auto p-1">
          {filtered.length === 0 ? (
            <p className="py-4 text-center text-sm text-muted-foreground">{emptyText}</p>
          ) : (
            filtered.map((option, index) => (
              <button
                key={option.value}
                type="button"
                data-option
                role="option"
                aria-selected={option.value === value}
                onClick={() => select(option.value)}
                onMouseEnter={() => setHighlight(index)}
                className={cn(
                  "flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm text-foreground outline-none",
                  index === highlight ? "bg-secondary" : "hover:bg-secondary",
                )}
              >
                <RiCheckLine
                  className={cn(
                    "size-4 shrink-0",
                    option.value === value ? "opacity-100" : "opacity-0",
                  )}
                  aria-hidden="true"
                />
                <span className="truncate">{option.label}</span>
              </button>
            ))
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}

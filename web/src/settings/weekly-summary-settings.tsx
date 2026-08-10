"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useId, useState } from "react";
import {
  getListMyOrgsQueryKey,
  useTestWeeklySummary,
  useUpdateWeeklySummarySettings,
} from "@/src/api/generated/condux";
import type { Org, UpdateWeeklySummaryRequest } from "@/src/api/generated/model";
import { TestChannelButton, TestStatus } from "@/src/channels/test-channel-button";
import { Combobox, type ComboboxOption } from "@/src/components/ui/combobox";

// Day-of-week options match .NET DayOfWeek (Sunday = 0). 2024-01-07 is a Sunday, so day d is that date + d.
const DAY_OPTIONS: ComboboxOption[] = Array.from({ length: 7 }, (_, day) => ({
  value: String(day),
  label: new Intl.DateTimeFormat(undefined, { weekday: "long", timeZone: "UTC" }).format(
    new Date(Date.UTC(2024, 0, 7 + day)),
  ),
}));

const HOUR_OPTIONS: ComboboxOption[] = Array.from({ length: 24 }, (_, hour) => ({
  value: String(hour),
  label: `${String(hour).padStart(2, "0")}:00`,
}));

// Every IANA zone the runtime knows (searchable in the combobox); "UTC" if the runtime lacks the API.
const TZ_OPTIONS: ComboboxOption[] = supportedTimezones().map((tz) => ({ value: tz, label: tz }));

function supportedTimezones(): string[] {
  const intl = Intl as { supportedValuesOf?: (key: "timeZone") => string[] };
  try {
    return intl.supportedValuesOf?.("timeZone") ?? ["UTC"];
  } catch {
    return ["UTC"];
  }
}

const optionLabel = (options: ComboboxOption[], value: string) =>
  options.find((option) => option.value === value)?.label ?? value;

// The Weekly summary section of the Notifications tab (ADR-0031): a per-org enable toggle + a send schedule
// (day / hour / timezone). Settings ride on the Org object (like the AI-fix settings), so this reads them
// there and PATCHes; each control submits the full set so one change never wipes another. Admin+ edits;
// members see a read-only summary. The test send goes to the requesting admin only.
export function WeeklySummarySettings({ org, canManage }: { org: Org; canManage: boolean }) {
  const translate = useTranslations("settings.weeklySummary");
  const common = useTranslations("common");
  const queryClient = useQueryClient();
  const update = useUpdateWeeklySummarySettings();

  const comboboxSearch = common("comboboxSearch");
  const comboboxEmpty = common("comboboxEmpty");
  const enabled = org.weeklySummaryEnabled ?? true;
  const dayOfWeek = org.weeklySummaryDow ?? 1;
  const hour = org.weeklySummaryHour ?? 9;
  const timezone = org.weeklySummaryTz ?? "UTC";

  const save = (patch: Partial<UpdateWeeklySummaryRequest>) => {
    if (update.isPending) {
      return;
    }
    update.mutate(
      { orgId: org.id, data: { enabled, dayOfWeek, hour, timezone, ...patch } },
      { onSuccess: () => queryClient.invalidateQueries({ queryKey: getListMyOrgsQueryKey() }) },
    );
  };

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      {canManage ? (
        <>
          <label className="flex cursor-pointer items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={enabled}
              disabled={update.isPending}
              onChange={() => save({ enabled: !enabled })}
            />
            <span className="text-foreground">{translate("enableLabel")}</span>
          </label>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            <ScheduleField
              label={translate("day")}
              value={String(dayOfWeek)}
              options={DAY_OPTIONS}
              disabled={!enabled || update.isPending}
              onChange={(value) => save({ dayOfWeek: Number(value) })}
              searchPlaceholder={comboboxSearch}
              emptyText={comboboxEmpty}
            />
            <ScheduleField
              label={translate("time")}
              value={String(hour)}
              options={HOUR_OPTIONS}
              disabled={!enabled || update.isPending}
              onChange={(value) => save({ hour: Number(value) })}
              searchPlaceholder={comboboxSearch}
              emptyText={comboboxEmpty}
            />
            <ScheduleField
              label={translate("timezone")}
              value={timezone}
              options={TZ_OPTIONS}
              disabled={!enabled || update.isPending}
              onChange={(value) => save({ timezone: value })}
              searchPlaceholder={comboboxSearch}
              emptyText={comboboxEmpty}
            />
          </div>
          <p className="text-xs text-muted-foreground">
            {enabled ? translate("scheduleHint") : translate("disabledHint")}
          </p>

          <SendTestButton orgId={org.id} />
          {update.isError ? (
            <p role="alert" className="text-sm text-error">
              {translate("saveFailed")}
            </p>
          ) : null}
        </>
      ) : (
        <p className="text-sm text-foreground">
          {enabled
            ? translate("readOnlyOn", {
                day: optionLabel(DAY_OPTIONS, String(dayOfWeek)),
                time: optionLabel(HOUR_OPTIONS, String(hour)),
                timezone,
              })
            : translate("readOnlyOff")}
        </p>
      )}
    </section>
  );
}

function ScheduleField({
  label,
  value,
  options,
  disabled,
  onChange,
  searchPlaceholder,
  emptyText,
}: {
  label: string;
  value: string;
  options: ComboboxOption[];
  disabled: boolean;
  onChange: (value: string) => void;
  searchPlaceholder: string;
  emptyText: string;
}) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1 text-xs text-muted-foreground">
      <label htmlFor={id}>{label}</label>
      <Combobox
        id={id}
        value={value}
        options={options}
        onValueChange={onChange}
        disabled={disabled}
        aria-label={label}
        searchPlaceholder={searchPlaceholder}
        emptyText={emptyText}
      />
    </div>
  );
}

function SendTestButton({ orgId }: { orgId: number }) {
  const test = useTestWeeklySummary();
  const [status, setStatus] = useState(TestStatus.Idle);

  const onSend = () => {
    setStatus(TestStatus.Sending);
    test.mutate(
      { orgId },
      {
        onSuccess: (response) =>
          setStatus(
            response.status === 200 && response.data.sent ? TestStatus.Sent : TestStatus.Failed,
          ),
        onError: () => setStatus(TestStatus.Failed),
      },
    );
  };

  // Reuses the channel test control (button + aria-live status); the div gives it its own line.
  return (
    <div>
      <TestChannelButton status={status} onSend={onSend} />
    </div>
  );
}

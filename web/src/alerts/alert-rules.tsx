"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListAlertRulesQueryKey,
  useCreateAlertRule,
  useListAlertRules,
} from "@/src/api/generated/condux";
import type { AlertRuleResponse } from "@/src/api/generated/model";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { AlertRuleRow } from "./alert-rule-row";
import { EventCheckboxes } from "./event-checkboxes";
import { LevelCheckboxes } from "./level-checkboxes";

// Alert rules for a project: create one, then manage each in a compact table (expand a row for its channels).
// Every mutation invalidates the list. The consumer's alert engine is what actually fires these.
export function AlertRules({ projectId }: { projectId: number }) {
  const translate = useTranslations("settings.alerts");
  const rules = useListAlertRules(projectId);

  return (
    <section>
      <div>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      <div className="mt-4">
        <CreateRuleForm projectId={projectId} />
      </div>

      <div className="mt-4">
        <RuleTableBody
          isPending={rules.isPending}
          isError={rules.isError}
          rules={rules.data?.data ?? []}
          projectId={projectId}
        />
      </div>
    </section>
  );
}

function RuleTableBody({
  isPending,
  isError,
  rules,
  projectId,
}: {
  isPending: boolean;
  isError: boolean;
  rules: AlertRuleResponse[];
  projectId: number;
}) {
  const translate = useTranslations("settings.alerts");
  if (isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (rules.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }
  return (
    <div className="overflow-x-auto rounded-lg border border-border">
      <table className="w-full text-left text-sm">
        <thead className="text-xs uppercase text-muted-foreground">
          <tr>
            <th className="w-8 py-2 pl-3" />
            <th className="py-2 pr-3 font-medium">{translate("name")}</th>
            <th className="py-2 pr-3 font-medium">{translate("firesOnColumn")}</th>
            <th className="py-2 pr-3 font-medium">{translate("channelsColumn")}</th>
            <th className="py-2 pr-3 font-medium">{translate("statusColumn")}</th>
            <th className="py-2 pr-3" />
          </tr>
        </thead>
        <tbody>
          {rules.map((rule) => (
            <AlertRuleRow key={rule.id} rule={rule} projectId={projectId} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CreateRuleForm({ projectId }: { projectId: number }) {
  const translate = useTranslations("settings.alerts");
  const queryClient = useQueryClient();
  const createRule = useCreateAlertRule();

  const [name, setName] = useState("");
  const [levels, setLevels] = useState<number[]>([4, 5]);
  const [events, setEvents] = useState<number[]>([1, 2]);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    if (name.trim() === "" || levels.length === 0 || events.length === 0) {
      return;
    }
    createRule.mutate(
      { projectId, data: { name: name.trim(), events, levels } },
      {
        onSuccess: () => {
          setName("");
          queryClient.invalidateQueries({ queryKey: getListAlertRulesQueryKey(projectId) });
        },
      },
    );
  };

  return (
    <form onSubmit={submit} className="rounded-lg border border-border bg-card p-4">
      <div className="flex flex-wrap items-end gap-4">
        <label className="flex flex-col gap-1 text-sm">
          <span className="text-xs uppercase text-muted-foreground">{translate("name")}</span>
          <input
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder={translate("namePlaceholder")}
            className={FIELD_CLASS}
          />
        </label>
        <EventCheckboxes events={events} onChange={setEvents} />
        <LevelCheckboxes levels={levels} onChange={setLevels} />
        <button type="submit" disabled={createRule.isPending} className={PRIMARY_BUTTON_CLASS}>
          {createRule.isPending ? translate("creating") : translate("create")}
        </button>
      </div>
    </form>
  );
}

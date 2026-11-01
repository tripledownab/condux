"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getGetLinearProjectQueryKey,
  useGetLinearProject,
  useListLinearLabels,
  useSetLinearMapping,
} from "@/src/api/generated/condux";
import type { LinearMappingResponse } from "@/src/api/generated/model";
import { Checkbox } from "@/src/components/ui/checkbox";
import { Combobox } from "@/src/components/ui/combobox";
import { lookUpCodeKey, readErrorCode } from "@/src/lib/api-errors";
import { toLabelOptions } from "./label-options";

// The refusals a save can name, each with what to do about it. Anything else is a failure to try again.
const SAVE_ERROR_KEYS: Record<string, string> = {
  linear_label_unknown: "saveLabelUnknown",
  linear_labels_same_group: "saveLabelsSameGroup",
};

// How a mapped project's tickets are made: the labels every ticket carries, and whether a new error opens
// one on its own. Each change saves the whole choice at once, which is how the API takes it, so a label
// can never outlive the team it belonged to. Members see the settings; admins change them.
export function LinearTicketSettings({
  projectId,
  mapping,
  canManage,
}: {
  projectId: number;
  mapping: LinearMappingResponse;
  canManage: boolean;
}) {
  const translate = useTranslations("linear");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const available = useListLinearLabels(projectId, { query: { enabled: canManage } });
  // The tab's own status query, already in the cache, carries today's count and the limit.
  const status = useGetLinearProject(projectId);
  const today = status.data?.status === 200 ? status.data.data : null;
  const save = useSetLinearMapping();
  // The combobox only picks; once picked, the label is added and the picker clears.
  const [picking, setPicking] = useState("");

  const chosen = mapping.labels.map((label) => label.id);
  const saveChoice = (labelIds: string[], autoCreate: boolean) =>
    save.mutate(
      { projectId, data: { teamId: mapping.teamId, labelIds, autoCreate } },
      {
        onSuccess: () =>
          queryClient.invalidateQueries({ queryKey: getGetLinearProjectQueryKey(projectId) }),
      },
    );
  const offered = available.data?.status === 200 ? available.data.data : null;
  const options = toLabelOptions(offered ?? [], chosen, (group, label) =>
    translate("labelInGroup", { group, label }),
  );
  const saveErrorKey = lookUpCodeKey(SAVE_ERROR_KEYS, readErrorCode(save.error)) ?? "saveFailed";
  // A label the project still has but Linear no longer offers was deleted or withdrawn there. The next
  // save drops it, and until then it is marked so the reason a ticket failed is on screen.
  const isGone = (id: string) => offered !== null && !offered.some((label) => label.id === id);

  return (
    <div className="flex flex-col items-start gap-3">
      <div className="flex flex-col gap-2">
        <span className="text-sm">{translate("labels")}</span>
        <div className="flex flex-wrap items-center gap-2">
          {mapping.labels.length === 0 ? (
            <span className="text-sm text-muted-foreground">{translate("noLabels")}</span>
          ) : (
            mapping.labels.map((label) => (
              <span
                key={label.id}
                className="flex items-center gap-1 rounded-md border border-border px-2 py-0.5 text-xs"
              >
                {label.name}
                {isGone(label.id) ? (
                  <span className="text-error">{translate("labelGone")}</span>
                ) : null}
                {canManage ? (
                  <button
                    type="button"
                    aria-label={translate("removeLabel", { label: label.name })}
                    disabled={save.isPending}
                    onClick={() =>
                      saveChoice(
                        chosen.filter((id) => id !== label.id),
                        mapping.autoCreate,
                      )
                    }
                    className="text-muted-foreground hover:text-foreground"
                  >
                    ×
                  </button>
                ) : null}
              </span>
            ))
          )}
        </div>
        {canManage ? (
          <Combobox
            id="linear-label"
            value={picking}
            onValueChange={(labelId) => {
              setPicking("");
              if (labelId) {
                saveChoice([...chosen, labelId], mapping.autoCreate);
              }
            }}
            options={options}
            placeholder={translate("addLabel")}
            className="w-72"
            disabled={save.isPending}
            searchPlaceholder={tCommon("comboboxSearch")}
            emptyText={available.isPending ? translate("loadingLabels") : tCommon("comboboxEmpty")}
          />
        ) : null}
      </div>
      <label htmlFor="linear-auto-create" className="flex items-start gap-2 text-sm">
        <Checkbox
          id="linear-auto-create"
          checked={mapping.autoCreate}
          disabled={!canManage || save.isPending}
          onCheckedChange={(checked) => saveChoice(chosen, checked === true)}
          className="mt-0.5"
        />
        <span className="flex flex-col gap-0.5">
          <span>{translate("autoCreate")}</span>
          <span className="text-xs text-muted-foreground">{translate("autoCreateHint")}</span>
        </span>
      </label>
      {mapping.autoCreate && today !== null ? (
        <p
          className={
            today.autoCreatedToday >= today.autoCreateDailyLimit
              ? "text-xs text-error"
              : "text-xs text-muted-foreground"
          }
        >
          {today.autoCreatedToday >= today.autoCreateDailyLimit
            ? translate("autoCreateLimitReached", { limit: today.autoCreateDailyLimit })
            : translate("autoCreatedToday", {
                count: today.autoCreatedToday,
                limit: today.autoCreateDailyLimit,
              })}
        </p>
      ) : null}
      {available.isError ? <p className="text-sm text-error">{translate("labelsFailed")}</p> : null}
      {save.isError ? <p className="text-sm text-error">{translate(saveErrorKey)}</p> : null}
    </div>
  );
}

"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getGetLinearProjectQueryKey,
  useDeleteLinearMapping,
  useListLinearTeams,
  useSetLinearMapping,
} from "@/src/api/generated/condux";
import type { LinearMappingResponse } from "@/src/api/generated/model";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox } from "@/src/components/ui/combobox";
import { LinearTicketSettings } from "./linear-ticket-settings";

// Which Linear team this project's tickets land in. The list is fetched live, and only while choosing,
// because which teams the app can reach is up to Linear's admins and changes without telling us.
export function LinearTeamChoice({
  projectId,
  mapping,
  canManage,
}: {
  projectId: number;
  mapping: LinearMappingResponse | null;
  canManage: boolean;
}) {
  const translate = useTranslations("linear");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const [choosing, setChoosing] = useState(false);
  const picking = choosing || mapping === null;
  const teams = useListLinearTeams(projectId, { query: { enabled: canManage && picking } });
  const [teamId, setTeamId] = useState(mapping?.teamId ?? "");
  const save = useSetLinearMapping();
  const unmap = useDeleteLinearMapping();
  const refresh = () =>
    queryClient.invalidateQueries({ queryKey: getGetLinearProjectQueryKey(projectId) });

  if (!picking && mapping !== null) {
    return (
      <div className="flex flex-col items-start gap-2">
        <p className="text-sm">{translate("mappedTo", { team: mapping.teamName })}</p>
        <LinearTicketSettings projectId={projectId} mapping={mapping} canManage={canManage} />
        {mapping.needsAttention ? (
          <p className="text-sm text-error">{translate("needsAttention")}</p>
        ) : null}
        {canManage ? (
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => setChoosing(true)}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("changeTeam")}
            </button>
            <button
              type="button"
              disabled={unmap.isPending}
              onClick={() => unmap.mutate({ projectId }, { onSuccess: refresh })}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("unmap")}
            </button>
          </div>
        ) : null}
      </div>
    );
  }

  if (!canManage) {
    return <p className="text-sm text-muted-foreground">{translate("noTeamYet")}</p>;
  }
  const options = (teams.data?.status === 200 ? teams.data.data : []).map((team) => ({
    value: team.id,
    label: `${team.name} (${team.key})`,
  }));

  return (
    <div className="flex flex-col items-start gap-2">
      <label htmlFor="linear-team" className="text-sm">
        {translate("chooseTeam")}
      </label>
      <Combobox
        id="linear-team"
        value={teamId}
        onValueChange={setTeamId}
        options={options}
        className="w-72"
        searchPlaceholder={tCommon("comboboxSearch")}
        emptyText={teams.isPending ? translate("loadingTeams") : tCommon("comboboxEmpty")}
      />
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          disabled={teamId === "" || save.isPending}
          className={PRIMARY_BUTTON_CLASS}
          onClick={() =>
            save.mutate(
              // A new team keeps the auto-create choice but not the labels, which may be the old team's own.
              {
                projectId,
                data: { teamId, labelIds: [], autoCreate: mapping?.autoCreate ?? false },
              },
              {
                onSuccess: () => {
                  setChoosing(false);
                  refresh();
                },
              },
            )
          }
        >
          {save.isPending ? translate("saving") : translate("saveTeam")}
        </button>
        {mapping !== null ? (
          <button
            type="button"
            onClick={() => setChoosing(false)}
            className={SECONDARY_BUTTON_CLASS}
          >
            {translate("cancel")}
          </button>
        ) : null}
      </div>
      {teams.isError ? <p className="text-sm text-error">{translate("teamsFailed")}</p> : null}
      {save.isError ? <p className="text-sm text-error">{translate("saveFailed")}</p> : null}
    </div>
  );
}

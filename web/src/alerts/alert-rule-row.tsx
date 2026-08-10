"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListAlertRulesQueryKey,
  useAddAlertChannel,
  useDeleteAlertRule,
  useUpdateAlertRule,
} from "@/src/api/generated/condux";
import type { AlertRuleResponse } from "@/src/api/generated/model";
import { AddChannelDialog } from "@/src/channels/add-channel-dialog";
import { channelKey } from "@/src/channels/format";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Badge } from "@/src/components/ui/badge";
import { levelMeta } from "@/src/issues/issue-format";
import { AlertChannels } from "./alert-channels";
import { eventKey } from "./alert-format";
import { EditRuleDialog } from "./edit-rule-dialog";

// One rule as a table row: name, what it fires on, its channels (type badges), status, and the rule actions
// (edit + create channel open modals; enable/disable + delete mutate in place). Expanding the row reveals the
// channel management panel underneath. Every mutation invalidates the rules list.
export function AlertRuleRow({ rule, projectId }: { rule: AlertRuleResponse; projectId: number }) {
  const translate = useTranslations("settings.alerts");
  const translateChannel = useTranslations("settings.channels");
  const translateLevel = useTranslations("issues.level");
  const queryClient = useQueryClient();
  const updateRule = useUpdateAlertRule();
  const deleteRule = useDeleteAlertRule();
  const addChannel = useAddAlertChannel();
  const [expanded, setExpanded] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [addOpen, setAddOpen] = useState(false);

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListAlertRulesQueryKey(projectId) });

  const toggleEnabled = () =>
    updateRule.mutate(
      {
        projectId,
        ruleId: rule.id,
        data: { name: rule.name, events: rule.events, levels: rule.levels, enabled: !rule.enabled },
      },
      { onSuccess: invalidate },
    );

  const events = rule.events.map((event) => translate(`event.${eventKey(event)}`)).join(", ");
  const levels = rule.levels.map((level) => translateLevel(levelMeta(level).key)).join(", ");

  return (
    <>
      <tr className="border-t border-border">
        <td className="py-2 pl-3 pr-2 align-top">
          <button
            type="button"
            onClick={() => setExpanded((value) => !value)}
            aria-label={translate("toggleChannels")}
            aria-expanded={expanded}
            className="text-muted-foreground hover:text-foreground"
          >
            {expanded ? "▾" : "▸"}
          </button>
        </td>
        <td className="py-2 pr-3 align-top font-medium text-foreground">{rule.name}</td>
        <td className="py-2 pr-3 align-top text-xs text-muted-foreground">
          {translate("firesOnCell", { events: events || translate("nothing"), level: levels })}
        </td>
        <td className="py-2 pr-3 align-top">
          <span className="flex flex-wrap gap-1">
            {rule.channels.length === 0 ? (
              <span className="text-xs text-muted-foreground">{translateChannel("empty")}</span>
            ) : (
              rule.channels.map((channel) => (
                <Badge key={channel.id} variant="secondary" className="uppercase">
                  {translateChannel(channelKey(channel.channel))}
                </Badge>
              ))
            )}
          </span>
        </td>
        <td className="py-2 pr-3 align-top">
          <Badge variant={rule.enabled ? "secondary" : "outline"}>
            {rule.enabled ? translate("statusEnabled") : translate("statusDisabled")}
          </Badge>
        </td>
        <td className="py-2 pr-3 align-top">
          <div className="flex flex-wrap items-center justify-end gap-2">
            <button
              type="button"
              onClick={() => setEditOpen(true)}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("edit")}
            </button>
            <button
              type="button"
              onClick={toggleEnabled}
              disabled={updateRule.isPending}
              className={SECONDARY_BUTTON_CLASS}
            >
              {rule.enabled ? translate("disable") : translate("enable")}
            </button>
            <button
              type="button"
              onClick={() => setAddOpen(true)}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("createChannel")}
            </button>
            <button
              type="button"
              onClick={() =>
                deleteRule.mutate({ projectId, ruleId: rule.id }, { onSuccess: invalidate })
              }
              disabled={deleteRule.isPending}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("delete")}
            </button>
          </div>
          <EditRuleDialog
            rule={rule}
            projectId={projectId}
            open={editOpen}
            onOpenChange={setEditOpen}
            onSaved={invalidate}
          />
          <AddChannelDialog
            open={addOpen}
            onOpenChange={setAddOpen}
            pending={addChannel.isPending}
            onAdd={(channel, target) =>
              addChannel.mutate(
                { projectId, ruleId: rule.id, data: { channel, target, template: null } },
                {
                  onSuccess: () => {
                    invalidate();
                    setAddOpen(false);
                    setExpanded(true);
                  },
                },
              )
            }
          />
        </td>
      </tr>
      {expanded ? (
        <tr className="border-t border-border bg-muted/30">
          <td />
          <td colSpan={5} className="py-3 pr-3">
            <AlertChannels channels={rule.channels} projectId={projectId} ruleId={rule.id} />
          </td>
        </tr>
      ) : null}
    </>
  );
}
